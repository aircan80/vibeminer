using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

// Internal provisional state only. Public outputs wrap the owned arrays without copying;
// neither output retains this aggregate or its caller-owned source stream.
internal sealed record M2SkinGeometryData(M2Input Input, ushort[] Lookup, ushort[] Triangles, uint[] Properties,
    M2Section[] Sections, M2Span BatchSpan, M2Span ShadowSpan, uint MaximumBones, int HeaderBytes,
    long RetainedBytes, M2Limits Limits);

internal static class M2SkinGeometryCore
{
    internal static async Task<M2SkinGeometryData> ReadAsync(Stream source, M2Model model, M2Limits limits,
        bool includeMaterialBuffers, CancellationToken token)
    {
        var input = new M2Input(source, limits, token); var budget = new M2Budget(limits.MaxOutputBytes, model.RetainedBytes);
        budget.Add(0, 1);
        var headerBytes = model.Version == 264 ? 48 : model.Version is (272 or 274) ? 56 : throw Error("UNSUPPORTED", "The supplied model has no admitted SKIN profile.");
        var header = await input.AtAsync(0, headerBytes);
        if (!header.AsSpan(0, 4).SequenceEqual("SKIN"u8)) throw Error("UNSUPPORTED", "An external SKIN profile is required.");
        M2Span S(string name, int at, int stride, int max) => input.Span(name, U32(header, at), U32(header, at + 4), stride, max, 0, input.Length, headerBytes);
        var lookupSpan = S("SKIN vertex lookup", 4, 2, limits.MaxVertexLookup);
        var triangleSpan = S("SKIN triangle indices", 12, 2, limits.MaxTriangleIndices);
        var propertiesSpan = S("SKIN bone properties", 20, 4, limits.MaxVertexLookup);
        var sectionSpan = S("SKIN sections", 28, 48, limits.MaxSections);
        var batchSpan = S("SKIN batches", 36, 24, limits.MaxBatches);
        var shadowSpan = headerBytes == 56 ? S("SKIN shadow batches", 48, 12, limits.MaxBatches) : new M2Span("SKIN shadow batches", 0, 0, 12);
        var maxBones = U32(header, 44);
        // Authenticated chunked 272 and 274 sources can retain zero here while declaring
        // nonempty section palettes. Preserve that raw fact, never invent a maximum.
        // This finite exception does not assert universal zero/sentinel semantics.
        var rawZeroHeader = model.IsChunked && model.Version is (272 or 274) && maxBones == 0;
        if (maxBones > 256) throw CountLimit($"The SKIN file's bone palette has {N(maxBones)} bones", 256);
        if (triangleSpan.Count % 3 != 0) throw Error("INVALID", "The SKIN triangle index count is not divisible by three.");
        if (propertiesSpan.Count != lookupSpan.Count) throw Error("INVALID", "SKIN needs one four-byte bone-property record per lookup vertex.");
        budget.Add(lookupSpan.Count, 2); budget.Add(triangleSpan.Count, 6); // retained ushort source + reserved uint geometry result
        budget.Add(propertiesSpan.Count, 4); budget.Add(sectionSpan.Count, 64);
        if (includeMaterialBuffers) { budget.Add(batchSpan.Count, 32); budget.Add(shadowSpan.Count, 16); }
        else budget.Add(1, 512); // Scope/declaration wrappers and bounded core bookkeeping for the new output.
        var lookup = new ushort[lookupSpan.Count]; var triangles = new ushort[triangleSpan.Count]; var properties = new uint[propertiesSpan.Count];
        var sections = new M2Section[sectionSpan.Count];
        await input.RecordsAsync(lookupSpan, (row, i) =>
        { var value = U16(row); if (value >= model.Vertices.Count) throw Error("INVALID", "A SKIN vertex lookup does not identify a model vertex."); lookup[i] = value; });
        await input.RecordsAsync(triangleSpan, (row, i) =>
        { var value = U16(row); if (value >= lookup.Length) throw Error("INVALID", "A SKIN triangle does not identify a lookup vertex."); triangles[i] = value; });
        await input.RecordsAsync(propertiesSpan, (row, i) => properties[i] = U32(row));
        await input.RecordsAsync(sectionSpan, (row, i) =>
        {
            var vertexStart = U16(row, 4); var vertexCount = U16(row, 6); var start = U16(row, 8) + ((uint)U16(row, 2) << 16); var count = U16(row, 10);
            if (vertexStart + (long)vertexCount > lookup.Length || start > triangles.Length || count > triangles.Length - (long)start
                || start % 3 != 0 || count % 3 != 0) throw Error("INVALID", "A SKIN section has invalid vertex/triangle ranges.");
            var boneCount = U16(row, 12); var boneStart = U16(row, 14); var influences = U16(row, 16);
            if (rawZeroHeader && boneCount > 256)
                throw CountLimit($"A SKIN section's bone palette has {N(boneCount)} bones", 256);
            if (influences > 4 || !rawZeroHeader && boneCount > maxBones || boneCount > 0 && boneStart + (long)boneCount > model.BoneLookup.Count)
                throw Error("INVALID", "A SKIN section has invalid bone-palette geometry.");
            var radius = F(row, 44); if (radius < 0) throw Error("INVALID", "A SKIN section sort radius is negative.");
            sections[i] = new(U16(row), U16(row, 2), vertexStart, vertexCount, start, count,
                boneCount, boneStart, influences, U16(row, 18), V3(row, 20), V3(row, 32), radius);
        });
        // Validate each selected triangle against its section's lookup span, not only the global table.
        foreach (var section in sections)
        {
            for (var n = section.TriangleStart; n < section.TriangleStart + section.TriangleCount; n++)
            {
                if ((n & 4095) == 0) token.ThrowIfCancellationRequested(); var v = triangles[n];
                if (v < section.VertexStart || v >= section.VertexStart + section.VertexCount) throw Error("INVALID", "A section triangle escapes its declared vertex span.");
            }
            await Task.Yield(); token.ThrowIfCancellationRequested();
        }
        return new(input, lookup, triangles, properties, sections, batchSpan, shadowSpan, maxBones, headerBytes, budget.Used, limits);
    }
}
