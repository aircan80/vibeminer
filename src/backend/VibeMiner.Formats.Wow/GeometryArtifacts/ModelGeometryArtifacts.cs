using System.Numerics;
using System.Security.Cryptography;
using static VibeMiner.Formats.Wow.GeometryArtifactEncoding;

namespace VibeMiner.Formats.Wow;

/// <summary>Original VMG1 derivative writer. Does not acquire or authorize its original sources.</summary>
public static partial class ModelGeometryArtifacts
{
    public const string Revision = ArtifactRevision;
    public const string LinePolicy = EdgePolicy;
    public static Task<GeometryArtifact> BuildAsync(M2GeometrySkin skin, GeometrySourceBinding binding,
        Stream output, CancellationToken token = default) =>
        BuildAsync(skin, binding, output, GeometryArtifactWork.Unbudgeted, token);

    public static async Task<GeometryArtifact> BuildAsync(M2GeometrySkin skin, GeometrySourceBinding binding,
        Stream output, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(skin); ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(budget);
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0)
            throw Invalid("The caller-owned output must be writable, seekable and empty at position zero.");
        byte[] bindingHash; var model = skin.Model; long sum = 0;
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            bindingHash = BindingHash(binding);
            if (skin.RetainedBytes > 8L << 20) throw Limit("Typed model/SKIN output exceeds the initial eight-MiB parser allowance.");
            foreach (var declaration in model.Declarations)
            {
                token.ThrowIfCancellationRequested();
                if (declaration.Name is not ("vertices" or "collisionTriangles" or "collisionVertices" or "collisionNormals" or "name") && declaration.Count > 8192)
                    throw Limit("Model metadata exceeds the initial geometry delivery profile.");
            }
            if (model.BoneFileDataIds.Count > 8192 || model.AnimationFiles.Count > 8192 || model.SkinFileDataIds.Count + model.LodSkinFileDataIds.Count > 32)
                throw Limit("Model dependency metadata exceeds the initial delivery profile.");
            if (!model.IsChunked || model.Version != 274 || model.SkeletonFileDataId != 0 || model.BoneFileDataIds.Any(id => id != 0))
                throw Unsupported("The artifact requires the finite geometry-only model profile.");
            if (model.SkinFileDataIds.Count == 0 || model.SkinFileDataIds[0] != binding.SkinFileDataId
                || binding.Model.DecodedBytes != model.SourceBytes || binding.Skin.DecodedBytes != skin.SourceBytes)
                throw Invalid("The exact main-SKIN identity or original decoded source length does not match the typed input.");
            foreach (var section in skin.Sections)
            { token.ThrowIfCancellationRequested(); sum = checked(sum + section.TriangleCount); }
            if (sum == 0) throw Unsupported("The fully parsed main SKIN has no declared triangles; no artifact is produced.");
            if (sum > MaximumIndices) throw Limit("Summed section expansion exceeds the initial artifact profile.");
            Counts(model.Vertices.Count, skin.VertexLookup.Count, skin.TriangleLookup.Count, (int)sum, skin.Sections.Count);
        }
        // Root-approved finite original CPU unit: up to131070 global +131070 section-index visits.
        // It checks cancellation every4096, but is synchronous; new serialization loops yield separately.
        await Task.Yield(); token.ThrowIfCancellationRequested();
        M2StaticGeometry geometry;
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            geometry = M2StaticGeometry.Create(model, skin, token);
        await Task.Yield(); token.ThrowIfCancellationRequested();
        Header h; byte[] header;
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            var bounds = new GeometryBounds(Canonical(geometry.Bounds.Minimum), Canonical(geometry.Bounds.Maximum)); BoundsValid(bounds);
            h = new Header(model.Vertices.Count, skin.VertexLookup.Count, skin.TriangleLookup.Count, (int)sum, skin.Sections.Count, bounds);
            if (h.Length > MaximumBytes) throw Limit("The derived artifact exceeds two MiB.");
            header = HeaderBytesFor(h, bindingHash);
        }
        await Task.Yield();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        await WriteAsync(header);
        for (var first = 0; first < model.Vertices.Count;)
        {
            var count = Math.Min(PositionWriteVertices, model.Vertices.Count - first);
            using (await GeometryArtifactWork.EnterAsync(budget, token))
            {
                for (var i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    var position = model.Vertices[first + i].Position;
                    if (!Finite(position)) throw Invalid("A source position is not finite.");
                    F(buffer, i * 12, position.X); F(buffer, i * 12 + 4, position.Y); F(buffer, i * 12 + 8, position.Z);
                }
            }
            await Task.Yield();
            await WriteAsync(buffer.AsMemory(0, count * 12)); first += count;
        }
        var sectionOrdinal = 0; uint triangle = 0;
        while (sectionOrdinal < geometry.Sections.Count)
        {
            var used = 0; var workBytes = 0;
            using (await GeometryArtifactWork.EnterAsync(budget, token))
            {
                while (sectionOrdinal < geometry.Sections.Count && used + 24 <= EdgeWriteBytes && workBytes + 100 <= BufferBytes)
                {
                    var section = geometry.Sections[sectionOrdinal]; workBytes += 64;
                    if (triangle >= section.TriangleCount) { sectionOrdinal++; triangle = 0; continue; }
                    if ((triangle & 511) == 0) token.ThrowIfCancellationRequested();
                    var n = checked((int)(section.TriangleStart + triangle));
                    var a = geometry.Indices[n]; var b = geometry.Indices[n + 1]; var c = geometry.Indices[n + 2];
                    U(buffer, used, a); U(buffer, used + 4, b); U(buffer, used + 8, b);
                    U(buffer, used + 12, c); U(buffer, used + 16, c); U(buffer, used + 20, a); used += 24; triangle += 3; workBytes += 36;
                }
            }
            await Task.Yield();
            if (used != 0) await WriteAsync(buffer.AsMemory(0, used));
        }
        var directoryBytes = 0; uint lineStart = 0;
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            for (var ordinal = 0; ordinal < geometry.Sections.Count; ordinal++)
            {
                token.ThrowIfCancellationRequested(); var section = geometry.Sections[ordinal];
                U(buffer, directoryBytes, (uint)ordinal); W(buffer, directoryBytes + 4, section.Id); W(buffer, directoryBytes + 6, section.Level);
                U(buffer, directoryBytes + 8, section.VertexStart); U(buffer, directoryBytes + 12, section.VertexCount); U(buffer, directoryBytes + 16, section.TriangleStart);
                U(buffer, directoryBytes + 20, section.TriangleCount); U(buffer, directoryBytes + 24, lineStart); U(buffer, directoryBytes + 28, (uint)section.TriangleCount * 2);
                directoryBytes += 32; lineStart += (uint)section.TriangleCount * 2;
            }
        }
        await Task.Yield();
        await WriteAsync(buffer.AsMemory(0, directoryBytes)); token.ThrowIfCancellationRequested();
        if (output.Position != h.Length || output.Length != h.Length) throw Invalid("Output length changed during derivative writing.");
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            var fileHash = hash.GetHashAndReset(); var sha = Convert.ToHexStringLower(fileHash); var bound = Convert.ToHexStringLower(bindingHash);
            var id = ArtifactId(bindingHash, fileHash, h.Length); var manifest = Manifest(binding, bound, id, sha, h);
            return new(binding, bound, id, sha, h.Length, manifest);
        }

        async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            using (await GeometryArtifactWork.EnterAsync(budget, token)) hash.AppendData(bytes.Span);
            await Task.Yield(); token.ThrowIfCancellationRequested();
            await output.WriteAsync(bytes, token);
            token.ThrowIfCancellationRequested(); await Task.Yield();
        }
    }
    private static byte[] HeaderBytesFor(Header h, byte[] binding)
    {
        var b = new byte[HeaderBytes]; "VMG1"u8.CopyTo(b); W(b, 4, 1); W(b, 6, HeaderBytes); U(b, 8, (uint)h.Length); U(b, 12, 1); binding.CopyTo(b, 16);
        U(b, 48, (uint)h.Vertices); U(b, 52, (uint)h.Lines); U(b, 56, (uint)h.Sections); U(b, 60, (uint)h.Lookup);
        U(b, 64, HeaderBytes); U(b, 68, (uint)h.Vertices * 12); U(b, 72, (uint)h.LineOffset); U(b, 76, (uint)h.Lines * 4);
        U(b, 80, (uint)h.DirectoryOffset); U(b, 84, (uint)h.Sections * 32);
        for (var i = 0; i < 3; i++) { F(b, 88 + i * 4, h.Bounds.Min[i]); F(b, 100 + i * 4, h.Bounds.Max[i]); }
        U(b, 112, (uint)h.SectionIndices); U(b, 116, 1); U(b, 120, (uint)h.GlobalIndices); return b;
    }
}
