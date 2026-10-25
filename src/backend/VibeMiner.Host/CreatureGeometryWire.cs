using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class CreatureGeometryWire
{
    internal sealed record Input(CreatureGeometryRequest? Prepare = null, CreatureGeometryReadRequest? Read = null,
        CreatureGeometryOpenRequest? Open = null, string? LeaseId = null);

    internal static Input Request(string method, JsonElement value)
    {
        string[] common = ["productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "creatureId", "slot"];
        var fields = method switch
        {
            "creatures.geometry.read" => common.Append("expectedGeometryArtifactId"),
            "creatures.geometry.prepare" => common,
            "creatures.geometry.open" => common.Append("geometryArtifactId"),
            "creatures.geometry.close" => ["leaseId"],
            _ => throw Invalid()
        };
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var allowed = fields.ToHashSet(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw Invalid();
        if (method == "creatures.geometry.close")
        {
            var id = Text(value, "leaseId");
            if (id.Length != 32 || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw Invalid();
            return new(LeaseId: id);
        }
        if (!value.TryGetProperty("slot", out var slot) || slot.ValueKind != JsonValueKind.Number || !slot.TryGetInt32(out var ordinal)) throw Invalid();
        var selection = new CreatureDisplayMetadataRequest(new(Text(value, "productId"), Text(value, "buildIdentity"), Text(value, "sourceRegion"),
            Text(value, "researchLocale")), Text(value, "artifactId"), Text(value, "creatureId"), ordinal);
        if (method == "creatures.geometry.read")
        {
            string? expected = null;
            if (value.TryGetProperty("expectedGeometryArtifactId", out var field) && field.ValueKind != JsonValueKind.Null)
                expected = field.ValueKind == JsonValueKind.String ? field.GetString() : throw Invalid();
            var request = new CreatureGeometryReadRequest(selection, expected); CreatureGeometryValidation.Validate(request); return new(Read: request);
        }
        if (method == "creatures.geometry.open")
        {
            var request = new CreatureGeometryOpenRequest(selection, Text(value, "geometryArtifactId"));
            CreatureGeometryValidation.Validate(request); return new(Open: request);
        }
        var prepare = new CreatureGeometryRequest(selection); CreatureGeometryValidation.Validate(prepare); return new(Prepare: prepare);
    }

    internal static object Result(CreatureGeometryResult value) => new
    {
        value.Context, value.ArtifactId, value.CreatureId, value.Slot, value.Policy, value.Opening, state = Name(value.State),
        metadata = new
        {
            state = Name(value.Metadata.State), value.Metadata.CreatureSource, value.Metadata.CreatureCoverage,
            selected = value.Metadata.Selected is { } selected ? new
            { selected.Slot, selected.DisplayId, selected.Probability, state = Name(selected.State), selected.DisplayField, selected.ProbabilityField } : null,
            targets = value.Metadata.Targets.Select(target => new
            { role = Name(target.Role), target.TableId, target.ArtifactId, target.Coverage, target.MatchCount, target.Row }),
            absence = value.Metadata.Absence is { } absence ? Name(absence) : null,
            value.Metadata.ModelId, value.Metadata.ModelFileDataId, value.Metadata.RequiredTableId, appearance = Name(value.Metadata.Appearance)
        },
        sources = value.Sources.Select(source => new
        {
            role = Name(source.Role), source.FileDataId, source.ContentKey, source.EncodingKey, source.EncodedSha256,
            source.EncodedBytes, source.DecodedBytes, source.FullVerifierRevision, source.AssociationSha256,
            source.VariantPolicy, source.EncodingLookupRevision, source.RootLookupRevision, source.EncodingSelectionPolicy
        }),
        manifest = value.Manifest is { } manifest ? Manifest(manifest) : null,
        appearance = value.Appearance is { } appearance ? Appearance(appearance) : null,
        problem = value.Problem is { } problem ? new
        { problem.Code, problem.Message, problem.Retryable, dependency = problem.Dependency is { } dependency ? Name(dependency) : null,
            target = problem.Target is { } target ? Name(target) : null } : null
    };

    internal static object Manifest(CreatureGeometryManifest value) => new
    {
        value.ArtifactId, value.BindingSha256, value.Sha256, value.ByteLength, value.FormatVersion, value.HeaderBytes,
        value.Scope, value.CoordinatePolicy, value.Evaluations, value.VertexCount, value.VertexLookupCount,
        value.SourceTriangleIndexCount, value.SectionIndexCount, value.LineIndexCount, value.SectionCount,
        value.Positions, value.LineIndices, value.Sections,
        // Single's shortest JSON spelling need not equal its exact Float32 value as a JS number.
        bounds = new
        {
            min = new[] { (double)value.Bounds.Min[0], (double)value.Bounds.Min[1], (double)value.Bounds.Min[2] },
            max = new[] { (double)value.Bounds.Max[0], (double)value.Bounds.Max[1], (double)value.Bounds.Max[2] }
        },
        value.ModelFileDataId, value.SkinFileDataId,
        value.MainSkinOrdinal, value.ModelReaderRevision, value.SkinReaderRevision, value.InterpretationPolicy, value.ArtifactRevision, value.EdgePolicy
    };
    internal static object Descriptor(CreatureGeometryLeaseDescriptor value) =>
        new { value.LeaseId, manifest = value.Appearance is { } appearance ? AppearanceManifest(appearance) : Manifest(value.Manifest) };
    internal static object Appearance(CreatureGeometryAppearance value) => new
    {
        value.Policy, state = Name(value.State),
        display = value.Display is { } display ? new { display.DisplayId, display.ExtendedDisplayInfoId, display.TextureVariations } : null,
        textures = value.Textures.Select(t => new
        {
            t.Ordinal, t.Type, binding = Name(t.Binding), t.Variation, t.FileDataId, state = Name(t.State),
            image = t.Image is { } image ? new { image.Width, image.Height, image.MipLevel, image.BaseWidth, image.BaseHeight, image.Encoding } : null,
            source = t.Source is { } source ? new { source.ContentKey, source.EncodingKey, source.EncodedSha256, source.EncodedBytes, source.DecodedBytes, source.FullVerifierRevision } : null
        }),
        effects = value.Effects is { } e ? new { e.Particles, e.Ribbons, e.Lights, e.ShadowBatches, e.UnbatchedSections, evaluation = "notDrawn" } : null,
        manifest = value.Manifest is { } manifest ? AppearanceManifest(manifest) : null,
        problem = value.Problem is { } problem ? new { problem.Code, problem.Message, problem.Retryable, problem.BatchOrdinal, problem.TextureOrdinal } : null
    };
    internal static object AppearanceManifest(CreatureGeometryAppearanceManifest value) => new
    {
        value.ArtifactId, value.BindingSha256, value.Sha256, value.ByteLength, value.FormatVersion, value.HeaderBytes, value.CoordinatePolicy,
        value.VertexCount, value.IndexCount, value.DrawCount, value.TextureCount, value.Positions, value.Uvs, value.Indices, value.Draws,
        value.Textures, value.Texels,
        bounds = new
        {
            min = new[] { (double)value.Bounds.Min[0], (double)value.Bounds.Min[1], (double)value.Bounds.Min[2] },
            max = new[] { (double)value.Bounds.Max[0], (double)value.Bounds.Max[1], (double)value.Bounds.Max[2] }
        },
        value.AlphaReference, value.DisplayId, value.GeometryBindingSha256, value.MaterialPolicy, value.TexturePolicy,
        value.ReaderRevision, value.DecoderRevision, value.ArtifactRevision
    };
    internal static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value)
        ? JsonNamingPolicy.CamelCase.ConvertName(value.ToString())
        : throw new DiscoveryException("CREATURE_GEOMETRY_RESULT_INVALID", "The geometry result uses an undefined state.");
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : throw Invalid();
    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose an exact selected Creature slot and bounded geometry request.");
}
