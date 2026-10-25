using System.Numerics;
using System.Text;
using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

/// <summary>Original static M2 reader. Layout research: pywowlib 55276dc and wow.export 1b625d0;
/// see docs/formats/m2-skin.md. No dependency resolution, pose evaluation or source authority.</summary>
public static class M2ModelReader
{
    public const string Revision = "m2-static-native-v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static async Task<M2Model> ReadAsync(Stream source, M2Limits? limits = null, CancellationToken token = default)
    {
        limits ??= new(); var input = new M2Input(source, limits, token); var budget = new M2Budget(limits.MaxOutputBytes);
        var magic = await input.AtAsync(0, 4); var chunked = !magic.AsSpan().SequenceEqual("MD20"u8);
        var chunks = new List<M2Chunk>(); var known = new Dictionary<string, M2Chunk>(StringComparer.Ordinal);
        long origin = 0, bytes = input.Length;
        if (chunked)
        {
            for (long at = 0; at < input.Length;)
            {
                token.ThrowIfCancellationRequested(); if (chunks.Count == limits.MaxChunks) throw Error("LIMIT", "M2 chunk count exceeds its bound.");
                var header = await input.AtAsync(at, 8);
                if (header.AsSpan(0, 4).IndexOfAnyExceptInRange((byte)'0', (byte)'Z') >= 0)
                    throw Error("UNSUPPORTED", "The M2 source has an unsupported container/chunk identifier.");
                var id = Encoding.ASCII.GetString(header, 0, 4); var count = U32(header, 4);
                if (count > input.Length - at - 8) throw Error("INVALID", "An M2 chunk exceeds its source.");
                var chunk = new M2Chunk(id, at + 8, count); chunks.Add(chunk); budget.Add(1, 128);
                if (!known.TryAdd(id, chunk)) throw Error("INVALID", $"Duplicate M2 chunk {id} is not admitted.");
                at += 8 + (long)count;
            }
            if (!known.TryGetValue("MD21", out var body)) throw Error("UNSUPPORTED", "The M2 container has no admitted MD21 model payload.");
            origin = body.Offset; bytes = body.ByteLength;
        }
        if (bytes < 304) throw Error("INVALID", "The MD20 base header is truncated.");
        var core = await input.AtAsync(origin, 304);
        if (!core.AsSpan(0, 4).SequenceEqual("MD20"u8)) throw Error("INVALID", "MD21 must contain a nested MD20 model.");
        var version = U32(core, 4);
        // Chunked 272 and 274 share one layout for everything read here (#75): checked on live retail 272 pairs.
        if ((!chunked && version != 264) || (chunked && version is not (272 or 274)))
            throw Error("UNSUPPORTED", $"M2 version {version} {(chunked ? "in a chunked MD21" : "in an unchunked MD20")} model is outside the admitted profiles (unchunked 264, chunked 272 and 274).");
        var flags = U32(core, 16); var headerBytes = (flags & 8) != 0 ? 312 : 304;
        if (bytes < headerBytes) throw Error("INVALID", "The flagged texture-combiner descriptor is truncated.");
        var declarations = new List<M2ArrayDeclaration>(); var spans = new Dictionary<string, M2Span>(StringComparer.Ordinal);
        void Declare(string name, int at, int stride, int maximum)
        {
            var count = U32(core, at); var offset = U32(core, at + 4);
            spans.Add(name, input.Span(name, count, offset, stride, maximum, origin, bytes, headerBytes));
            declarations.Add(new(name, count, offset, stride)); budget.Add(1, 128);
        }
        Declare("name", 8, 1, limits.MaxNameBytes);
        Declare("globalSequences", 20, 4, limits.MaxMetadataEntries); Declare("sequences", 28, 64, limits.MaxMetadataEntries);
        Declare("sequenceLookup", 36, 2, limits.MaxMetadataEntries); Declare("bones", 44, 88, limits.MaxMetadataEntries);
        Declare("keyBones", 52, 2, limits.MaxMetadataEntries); Declare("vertices", 60, 48, limits.MaxVertices);
        var viewCount = U32(core, 68); if (viewCount > limits.MaxSkinProfiles) throw Error("LIMIT", "The M2 main-view count exceeds its bound.");
        Declare("colors", 72, 40, limits.MaxMetadataEntries); Declare("textures", 80, 16, limits.MaxTextures);
        Declare("weights", 88, 20, limits.MaxMetadataEntries); Declare("transforms", 96, 60, limits.MaxMetadataEntries);
        Declare("replacements", 104, 2, limits.MaxMetadataEntries); Declare("materials", 112, 4, limits.MaxMaterials);
        Declare("boneLookup", 120, 2, limits.MaxMetadataEntries); Declare("textureCombos", 128, 2, limits.MaxMetadataEntries);
        Declare("textureCoordinates", 136, 2, limits.MaxMetadataEntries); Declare("weightLookup", 144, 2, limits.MaxMetadataEntries);
        Declare("transformLookup", 152, 2, limits.MaxMetadataEntries);
        var declaredBounds = Bounds(core, 160); _ = Bounds(core, 188);
        Declare("collisionTriangles", 216, 2, limits.MaxTriangleIndices); Declare("collisionVertices", 224, 12, limits.MaxVertices);
        Declare("collisionNormals", 232, 12, limits.MaxVertices); Declare("attachments", 240, 40, limits.MaxMetadataEntries);
        Declare("attachmentLookup", 248, 2, limits.MaxMetadataEntries); Declare("events", 256, 0, limits.MaxMetadataEntries);
        Declare("lights", 264, 0, limits.MaxMetadataEntries); Declare("cameras", 272, 0, limits.MaxMetadataEntries);
        Declare("cameraLookup", 280, 2, limits.MaxMetadataEntries); Declare("ribbons", 288, 0, limits.MaxMetadataEntries);
        Declare("particles", 296, 0, limits.MaxMetadataEntries);
        if (headerBytes == 312)
        {
            var combiner = await input.AtAsync(origin + 304, 8);
            var count = U32(combiner); var offset = U32(combiner, 4);
            spans.Add("combiners", input.Span("combiners", count, offset, 2, limits.MaxMetadataEntries, origin, bytes, headerBytes));
            declarations.Add(new("combiners", count, offset, 2)); budget.Add(1, 128);
        }
        var name = await NameAsync(input, spans["name"], budget);
        var vertexSpan = spans["vertices"]; budget.Add(vertexSpan.Count, 48); var vertices = new M2Vertex[vertexSpan.Count];
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        await input.RecordsAsync(vertexSpan, (row, i) =>
        {
            var position = V3(row); vertices[i] = new(position, U32(row, 12), U32(row, 16), V3(row, 20), V2(row, 32), V2(row, 40));
            min = Vector3.Min(min, position); max = Vector3.Max(max, position);
        });
        M2Bounds? computed = null;
        if (vertices.Length != 0)
        {
            var dx = (double)max.X - min.X; var dy = (double)max.Y - min.Y; var dz = (double)max.Z - min.Z;
            var radius = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2;
            if (radius > float.MaxValue) throw Error("LIMIT", "Computed geometry bounds exceed the finite output representation.");
            computed = new(min, max, (float)radius);
        }
        var textureSpan = spans["textures"]; budget.Add(textureSpan.Count, 64); var textureRows = new (uint Type, uint Flags, uint Count, uint Offset)[textureSpan.Count];
        await input.RecordsAsync(textureSpan, (row, i) => textureRows[i] = (U32(row), U32(row, 4), U32(row, 8), U32(row, 12)));
        var txids = await ChunkIdsAsync("TXID", textureSpan.Count, false);
        if (chunked && textureSpan.Count > 0 && txids is null) throw Error("UNSUPPORTED", "The modern M2 profile needs an exact TXID texture-ordinal declaration.");
        var textures = new M2Texture[textureSpan.Count];
        for (var i = 0; i < textures.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var row = textureRows[i];
            var filename = await NameAsync(input, input.Span("texture filename", row.Count, row.Offset, 1, limits.MaxNameBytes, origin, bytes, headerBytes), budget);
            textures[i] = new(i, row.Type, row.Flags, filename, txids is null ? null : txids[i]);
        }
        budget.Add(spans["materials"].Count, 4); var materials = new M2Material[spans["materials"].Count];
        await input.RecordsAsync(spans["materials"], (row, i) => materials[i] = new(U16(row), U16(row, 2)));
        budget.Add(spans["bones"].Count, 32); var bones = new M2Bone[spans["bones"].Count];
        await input.RecordsAsync(spans["bones"], (row, i) =>
        {
            var parent = I16(row, 8); if (parent < -1 || parent >= bones.Length || parent == i) throw Error("INVALID", "A bone has an invalid parent index.");
            bones[i] = new(I32(row), U32(row, 4), parent, U16(row, 10), V3(row, 76));
        });
        var textureCombos = await WordsAsync("textureCombos"); var coordWords = await WordsAsync("textureCoordinates");
        budget.Add(coordWords.Length, 2); var coordinates = new short[coordWords.Length];
        for (var i = 0; i < coordinates.Length; i++) { token.ThrowIfCancellationRequested(); coordinates[i] = unchecked((short)coordWords[i]); }
        var weights = await WordsAsync("weightLookup"); var transforms = await WordsAsync("transformLookup"); var boneLookup = await WordsAsync("boneLookup");
        var allSkins = await ChunkIdsAsync("SFID", (int)viewCount, true);
        if (chunked && viewCount > 0 && allSkins is null) throw Error("UNSUPPORTED", "The modern model has no exact main-SKIN dependency declaration.");
        allSkins ??= []; budget.Add(allSkins.Length, 4);
        var skins = allSkins.Take((int)viewCount).ToArray(); var lodSkins = allSkins.Skip((int)viewCount).ToArray();
        uint skeleton = 0;
        if (known.TryGetValue("SKID", out var sk))
        { if (sk.ByteLength != 4) throw Error("INVALID", "SKID must contain exactly one declared skeleton ID."); skeleton = U32(await input.AtAsync(sk.Offset, 4)); }
        var boneFiles = await ChunkIdsAsync("BFID", null, true) ?? [];
        M2AnimationFile[] animationFiles = [];
        if (known.TryGetValue("AFID", out var af))
        {
            if (af.ByteLength % 8 != 0) throw Error("INVALID", "AFID has a partial animation dependency record.");
            var count = af.ByteLength / 8; if (count > limits.MaxMetadataEntries) throw Error("LIMIT", "AFID exceeds its dependency limit.");
            budget.Add(count, 8); animationFiles = new M2AnimationFile[(int)count];
            await input.RecordsAsync(new("AFID", af.Offset, (int)count, 8), (row, i) => animationFiles[i] = new(U16(row), U16(row, 2), U32(row, 4)));
        }
        await input.FinishAsync();
        return new(version, chunked, flags, name, input.Length, origin, bytes, viewCount, vertices, textures, materials, bones,
            textureCombos, coordinates, weights, transforms, boneLookup, skins, lodSkins, skeleton, boneFiles, animationFiles,
            declarations.ToArray(), chunks.ToArray(), declaredBounds, computed, budget.Used, limits);

        async Task<ushort[]> WordsAsync(string field)
        {
            var span = spans[field]; budget.Add(span.Count, 2); var values = new ushort[span.Count];
            await input.RecordsAsync(span, (row, i) => values[i] = U16(row)); return values;
        }
        async Task<uint[]?> ChunkIdsAsync(string id, int? expected, bool allowExtra)
        {
            if (!known.TryGetValue(id, out var chunk)) return null;
            if (chunk.ByteLength % 4 != 0) throw Error("INVALID", $"The {id} dependency chunk has a partial ID.");
            var count = chunk.ByteLength / 4; var maximum = id == "TXID" ? limits.MaxTextures : id == "SFID" ? limits.MaxSkinProfiles : limits.MaxMetadataEntries;
            if (count > maximum) throw Error("LIMIT", $"The {id} dependency count exceeds its limit.");
            if (expected is not null && (count < expected || !allowExtra && count != expected)) throw Error("INVALID", $"The {id} count differs from model ordinals.");
            budget.Add(count, 4); var ids = new uint[(int)count];
            await input.RecordsAsync(new(id, chunk.Offset, (int)count, 4), (row, i) => ids[i] = U32(row)); return ids;
        }
    }
    private static async Task<string> NameAsync(M2Input input, M2Span span, M2Budget budget)
    {
        if (span.Count == 0) return ""; budget.Add(span.Count, 2); var bytes = await input.AtAsync(span.Offset, span.Count);
        if (bytes[^1] != 0 || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0)) throw Error("INVALID", "A declared model name lacks its exact terminating NUL.");
        try { return Utf8.GetString(bytes, 0, bytes.Length - 1); }
        catch (DecoderFallbackException) { throw Error("UNSUPPORTED", "The declared model name is not supported strict UTF-8 text."); }
    }
}
