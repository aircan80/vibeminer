using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using static VibeMiner.Formats.Wow.GeometryArtifactEncoding;

namespace VibeMiner.Formats.Wow;

/// <summary>One decoded texture slot: the exact verified source, the chosen mip and its sampler wrap bits.</summary>
public sealed record AppearanceTextureInput(uint FileDataId, GeometryOriginalSource Source, int Width, int Height, int MipLevel,
    int BaseWidth, int BaseHeight, Blp2Encoding Encoding, bool WrapU, bool WrapV);
/// <summary>Display-keyed appearance identity. DisplayFields are ExtendedDisplayInfoID then TextureVariationFileDataID[0..3].</summary>
public sealed record AppearanceBinding(string GeometryBindingSha256, uint DisplayId, IReadOnlyList<int> DisplayFields,
    IReadOnlyList<AppearanceTextureInput> Textures);
public sealed record AppearanceDrawInput(int BatchOrdinal, int SectionIndex, int TextureOrdinal, int Slot, bool AlphaKey,
    bool TwoSided, bool DepthTest, bool DepthWrite);
public sealed record AppearanceManifest(string ArtifactId, string BindingSha256, string Sha256, long ByteLength, int FormatVersion,
    int HeaderBytes, string CoordinatePolicy, int VertexCount, int IndexCount, int DrawCount, int TextureCount,
    GeometryBufferRegion Positions, GeometryBufferRegion Uvs, GeometryBufferRegion Indices, GeometryBufferRegion Draws,
    GeometryBufferRegion Textures, GeometryBufferRegion Texels, GeometryBounds Bounds, int AlphaReference, uint DisplayId,
    string GeometryBindingSha256, string MaterialPolicy, string TexturePolicy, string ReaderRevision, string DecoderRevision,
    string ArtifactRevision);
public sealed record AppearanceArtifact(AppearanceBinding Binding, string BindingSha256, string ArtifactId, string Sha256,
    long ByteLength, AppearanceManifest Manifest);

/// <summary>Original VMA1 derivative: native positions, UV0, the drawn batches' triangles and RGBA8 texels.
/// It does not acquire, authorize or decode any source; see contracts/geometry-vma1-v1.md.</summary>
public static class ModelAppearanceArtifacts
{
    public const string Revision = "model-appearance-vma1-v1";
    public const string TexturePolicy = "blp2-largest-mip-within-1024-rgba8-v1";
    public const int MaximumTextureSide = 1024, MaximumTextures = 32, MaximumDraws = 512;
    public const long MaximumTexelBytes = 8L << 20, MaximumBytes = 10L << 20, MaximumTextureSourceBytes = 16L << 20;
    internal const int Header = 160;
    private static DiscoveryException Bad(string detail) => new("GEOMETRY_ARTIFACT_INVALID", detail);

    /// <summary>Chooses one declared level per distinct image (D091): start at the largest level with both sides at most
    /// 1,024 px, then, while the slots together exceed <paramref name="budget"/>, step the image with the largest current
    /// level (lowest index on ties) down one declared level. Null when even the smallest declared levels exceed the budget;
    /// <paramref name="noFit"/> names an image with no level within 1,024 px (else -1). Nothing is resized.</summary>
    public static int[]? ChooseMips(IReadOnlyList<IReadOnlyList<Blp2Mip>> images, IReadOnlyList<int> slots, out int noFit, long budget = MaximumTexelBytes)
    {
        ArgumentNullException.ThrowIfNull(images); ArgumentNullException.ThrowIfNull(slots);
        if (images.Count != slots.Count || slots.Any(n => n < 1)) throw new ArgumentException("Every image needs its slot count.", nameof(slots));
        var levels = new int[images.Count]; noFit = -1;
        for (var i = 0; i < images.Count; i++)
        {
            levels[i] = images[i].ToList().FindIndex(m => m.Width <= MaximumTextureSide && m.Height <= MaximumTextureSide);
            if (levels[i] < 0) { noFit = i; return null; }
        }
        long Bytes(int i) => (long)images[i][levels[i]].Width * images[i][levels[i]].Height * 4;
        long Total() { long sum = 0; for (var i = 0; i < images.Count; i++) sum += Bytes(i) * slots[i]; return sum; }
        while (Total() > budget)
        {
            var step = -1;
            for (var i = 0; i < images.Count; i++)
                if (levels[i] + 1 < images[i].Count && (step < 0 || Bytes(i) > Bytes(step))) step = i;
            if (step < 0) return null;
            levels[step]++;
        }
        return levels;
    }

    public static string ComputeBindingSha256(AppearanceBinding binding, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return Convert.ToHexStringLower(BindingHash(binding)); }

    public static async Task<AppearanceArtifact> BuildAsync(M2GeometrySkin skin, IReadOnlyList<AppearanceDrawInput> draws,
        AppearanceBinding binding, IReadOnlyList<byte[]> texels, Stream output, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(skin); ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(texels); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(budget);
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0)
            throw Bad("The caller-owned output must be writable, seekable and empty at position zero.");
        var model = skin.Model; byte[] bindingHash; Layout layout; uint[] indices; var drawRecords = new byte[draws.Count * 32];
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            bindingHash = BindingHash(binding);
            if (texels.Count != binding.Textures.Count) throw Bad("Every texture slot needs exactly one decoded image.");
            for (var i = 0; i < texels.Count; i++)
                if (texels[i] is null || texels[i].Length != (long)binding.Textures[i].Width * binding.Textures[i].Height * 4) throw Bad("A decoded image differs from its declared mip size.");
            if (draws.Count is < 1 or > MaximumDraws) throw Limit("The appearance draws exceed the artifact profile.");
            long total = 0; var previous = -1;
            foreach (var draw in draws)
            {
                token.ThrowIfCancellationRequested();
                if (draw is null || draw.BatchOrdinal <= previous || draw.SectionIndex < 0 || draw.SectionIndex >= skin.Sections.Count || draw.Slot < 0 ||
                    draw.Slot >= binding.Textures.Count || draw.TextureOrdinal is < 0 or >= MaximumTextures || skin.Sections[draw.SectionIndex].TriangleCount == 0)
                    throw Bad("A draw names no drawable section or decoded texture slot.");
                previous = draw.BatchOrdinal; total += skin.Sections[draw.SectionIndex].TriangleCount;
            }
            if (total > MaximumIndices) throw Limit("The drawn triangles exceed the artifact profile.");
            if (model.Vertices.Count is < 1 or > MaximumVertices) throw Limit("The model vertices exceed the artifact profile.");
            layout = new(model.Vertices.Count, (int)total, draws.Count, binding.Textures.Select(t => (long)t.Width * t.Height * 4).ToArray());
            if (layout.Length > MaximumBytes) throw Limit("The appearance artifact exceeds ten MiB.");
            indices = new uint[total];
        }
        await Task.Yield();
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            uint at = 0;
            for (var d = 0; d < draws.Count; d++)
            {
                var draw = draws[d]; var section = skin.Sections[draw.SectionIndex]; var start = at;
                for (var n = section.TriangleStart; n < section.TriangleStart + section.TriangleCount; n++)
                {
                    if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
                    var vertex = (uint)skin.VertexLookup[skin.TriangleLookup[(int)n]]; indices[at++] = vertex;
                    var p = model.Vertices[(int)vertex].Position; min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
                var r = d * 32; U(drawRecords, r, (uint)d); U(drawRecords, r + 4, (uint)draw.BatchOrdinal); U(drawRecords, r + 8, (uint)draw.SectionIndex);
                U(drawRecords, r + 12, start); U(drawRecords, r + 16, at - start); U(drawRecords, r + 20, (uint)draw.Slot); U(drawRecords, r + 24, (uint)draw.TextureOrdinal);
                U(drawRecords, r + 28, Flags(draw));
            }
        }
        await Task.Yield();
        var bounds = new GeometryBounds(Canonical(min), Canonical(max)); BoundsValid(bounds);
        var header = HeaderFor(layout, bindingHash, bounds, binding.DisplayId);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[BufferBytes];
        await WriteAsync(header);
        foreach (var uv in new[] { false, true })
            for (var first = 0; first < layout.Vertices;)
            {
                var stride = uv ? 8 : 12; var count = Math.Min(BufferBytes / stride, layout.Vertices - first);
                using (await GeometryArtifactWork.EnterAsync(budget, token))
                    for (var i = 0; i < count; i++)
                    {
                        if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); var v = model.Vertices[first + i];
                        if (uv) { if (!float.IsFinite(v.Uv0.X) || !float.IsFinite(v.Uv0.Y)) throw Bad("A source UV is not finite."); F(buffer, i * 8, v.Uv0.X); F(buffer, i * 8 + 4, v.Uv0.Y); }
                        else { if (!Finite(v.Position)) throw Bad("A source position is not finite."); F(buffer, i * 12, v.Position.X); F(buffer, i * 12 + 4, v.Position.Y); F(buffer, i * 12 + 8, v.Position.Z); }
                    }
                await WriteAsync(buffer.AsMemory(0, count * stride)); first += count;
            }
        for (var first = 0; first < indices.Length;)
        {
            var count = Math.Min(BufferBytes / 4, indices.Length - first);
            using (await GeometryArtifactWork.EnterAsync(budget, token)) for (var i = 0; i < count; i++) U(buffer, i * 4, indices[first + i]);
            await WriteAsync(buffer.AsMemory(0, count * 4)); first += count;
        }
        await WriteAsync(drawRecords);
        await WriteAsync(TextureDirectory(binding, layout));
        foreach (var image in texels)
            for (var at = 0; at < image.Length; at += BufferBytes) await WriteAsync(image.AsMemory(at, Math.Min(BufferBytes, image.Length - at)));
        if (output.Position != layout.Length || output.Length != layout.Length) throw Bad("Output length changed during derivative writing.");
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            var fileHash = hash.GetHashAndReset(); var sha = Convert.ToHexStringLower(fileHash); var bound = Convert.ToHexStringLower(bindingHash);
            var id = ArtifactId(bindingHash, fileHash, layout.Length);
            return new(binding, bound, id, sha, layout.Length, Manifest(binding, bound, id, sha, layout, bounds));
        }
        async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            using (await GeometryArtifactWork.EnterAsync(budget, token)) hash.AppendData(bytes.Span);
            await Task.Yield(); token.ThrowIfCancellationRequested(); await output.WriteAsync(bytes, token); token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>One sequential pass: full SHA, header, finite positions/UVs, index range, draw partition, texture
    /// directory against the binding, tight drawn bounds and EOF. Holds one positions buffer (at most 768 KiB).</summary>
    public static async Task VerifyAsync(Stream source, AppearanceArtifact expected, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(budget);
        Layout layout; byte[] bindingHash, fileHash;
        using (await GeometryArtifactWork.EnterAsync(budget, token)) (layout, bindingHash, fileHash) = Validate(expected);
        if (!source.CanRead || !source.CanSeek || source.Position != 0 || source.Length != layout.Length)
            throw Bad("The caller-owned appearance input must be readable, seekable, complete and at position zero.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[BufferBytes];
        var header = new byte[Header]; await ReadAsync(header);
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            if (!header.AsSpan().SequenceEqual(HeaderFor(layout, bindingHash, expected.Manifest.Bounds, expected.Binding.DisplayId))) throw Bad("VMA1 header differs from its exact descriptor.");
        var positions = new byte[layout.Vertices * 12];
        for (var at = 0; at < positions.Length; at += BufferBytes) await ReadAsync(positions.AsMemory(at, Math.Min(BufferBytes, positions.Length - at)));
        for (var at = 0; at < positions.Length; at += BufferBytes)
        {
            using (await GeometryArtifactWork.EnterAsync(budget, token))
                for (var i = at; i < Math.Min(positions.Length, at + BufferBytes); i += 4) if (!float.IsFinite(F(positions, i))) throw Bad("An appearance position is not finite.");
            await Task.Yield();
        }
        for (var remaining = layout.Vertices * 8; remaining > 0;)
        {
            var count = Math.Min(BufferBytes, remaining); await ReadAsync(buffer.AsMemory(0, count)); remaining -= count;
            using (await GeometryArtifactWork.EnterAsync(budget, token)) for (var i = 0; i < count; i += 4) if (!float.IsFinite(F(buffer, i))) throw Bad("An appearance UV is not finite.");
        }
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var remaining = layout.Indices * 4; remaining > 0;)
        {
            var count = Math.Min(BufferBytes, remaining); await ReadAsync(buffer.AsMemory(0, count)); remaining -= count;
            using (await GeometryArtifactWork.EnterAsync(budget, token))
                for (var i = 0; i < count; i += 4)
                {
                    if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                    var index = U(buffer, i); if (index >= layout.Vertices) throw Bad("An appearance index names no vertex.");
                    var p = V(positions, (int)index * 12); min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
        }
        var directory = new byte[layout.Draws * 32]; await ReadAsync(directory);
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            long cumulative = 0; long previous = -1;
            for (var d = 0; d < layout.Draws; d++)
            {
                var r = d * 32; var batch = U(directory, r + 4); var count = U(directory, r + 16);
                if (U(directory, r) != d || batch <= previous || batch >= MaximumDraws || U(directory, r + 8) > 255 || U(directory, r + 12) != cumulative ||
                    count == 0 || count % 3 != 0 || U(directory, r + 20) >= layout.Slots.Length || U(directory, r + 24) >= MaximumTextures || (U(directory, r + 28) & ~15u) != 0)
                    throw Bad("A draw record violates the exact draw partition.");
                previous = batch; cumulative += count;
            }
            if (cumulative != layout.Indices || !Same(new GeometryBounds(Canonical(min), Canonical(max)), expected.Manifest.Bounds))
                throw Bad("The draw partition or tight drawn bounds differ from the declaration.");
        }
        var textures = new byte[layout.Slots.Length * 32]; await ReadAsync(textures);
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            if (!textures.AsSpan().SequenceEqual(TextureDirectory(expected.Binding, layout))) throw Bad("The texture directory differs from its binding.");
        for (var remaining = layout.TexelBytes; remaining > 0;)
        { var count = (int)Math.Min(BufferBytes, remaining); await ReadAsync(buffer.AsMemory(0, count)); remaining -= count; }
        if (source.Position != layout.Length || await source.ReadAsync(buffer.AsMemory(0, 1), token) != 0 || source.Length != layout.Length)
            throw Bad("The appearance artifact has trailing bytes or changed length.");
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), fileHash)) throw Bad("The complete appearance SHA256 does not match its descriptor.");
        async ValueTask ReadAsync(Memory<byte> bytes)
        {
            token.ThrowIfCancellationRequested();
            try { await source.ReadExactlyAsync(bytes, token); }
            catch (EndOfStreamException) { throw Bad("The VMA1 artifact is truncated."); }
            using (await GeometryArtifactWork.EnterAsync(budget, token)) hash.AppendData(bytes.Span);
            await Task.Yield(); token.ThrowIfCancellationRequested();
        }
    }

    private static (Layout, byte[], byte[]) Validate(AppearanceArtifact descriptor)
    {
        if (descriptor?.Manifest is null || descriptor.Binding is null) throw Bad("An engine appearance descriptor is required.");
        var bindingHash = BindingHash(descriptor.Binding); var bound = Convert.ToHexStringLower(bindingHash); var fileHash = Hex(descriptor.Sha256, 32);
        var m = descriptor.Manifest; BoundsValid(m.Bounds);
        if (m.VertexCount is < 1 or > MaximumVertices || m.IndexCount is < 3 or > MaximumIndices || m.IndexCount % 3 != 0 || m.DrawCount is < 1 or > MaximumDraws)
            throw Bad("The appearance counts are outside the artifact profile.");
        var layout = new Layout(m.VertexCount, m.IndexCount, m.DrawCount, descriptor.Binding.Textures.Select(t => (long)t.Width * t.Height * 4).ToArray());
        if (descriptor.BindingSha256 != bound || descriptor.ByteLength != layout.Length || descriptor.ArtifactId != ArtifactId(bindingHash, fileHash, layout.Length) ||
            m != Manifest(descriptor.Binding, bound, descriptor.ArtifactId, descriptor.Sha256, layout, m.Bounds))
            throw Bad("The canonical appearance descriptor, revisions or manifest fields do not match.");
        return (layout, bindingHash, fileHash);
    }
    private static uint Flags(AppearanceDrawInput draw) => (draw.AlphaKey ? 1u : 0) | (draw.TwoSided ? 2u : 0) | (draw.DepthTest ? 0 : 4u) | (draw.DepthWrite ? 0 : 8u);
    internal static byte EncodingCode(Blp2Encoding encoding) => encoding switch
    { Blp2Encoding.Paletted => 1, Blp2Encoding.Bc1 => 2, Blp2Encoding.Bc2 => 3, Blp2Encoding.Bc3 => 4, Blp2Encoding.Bgra8 => 5, _ => throw Bad("Unknown texture encoding.") };
    private static byte[] TextureDirectory(AppearanceBinding binding, Layout layout)
    {
        var bytes = new byte[layout.Slots.Length * 32]; long offset = layout.TexelOffset;
        for (var i = 0; i < layout.Slots.Length; i++)
        {
            var t = binding.Textures[i]; var r = i * 32;
            U(bytes, r, (uint)i); U(bytes, r + 4, t.FileDataId); U(bytes, r + 8, (uint)t.Width); U(bytes, r + 12, (uint)t.Height);
            U(bytes, r + 16, (uint)offset); U(bytes, r + 20, (uint)layout.Slots[i]); bytes[r + 24] = (byte)t.MipLevel; bytes[r + 25] = EncodingCode(t.Encoding);
            bytes[r + 26] = (byte)((t.WrapU ? 1 : 0) | (t.WrapV ? 2 : 0)); W(bytes, r + 28, (ushort)t.BaseWidth); W(bytes, r + 30, (ushort)t.BaseHeight);
            offset += layout.Slots[i];
        }
        return bytes;
    }
    private static byte[] HeaderFor(Layout l, byte[] binding, GeometryBounds bounds, uint display)
    {
        var b = new byte[Header]; "VMA1"u8.CopyTo(b); W(b, 4, 1); W(b, 6, Header); U(b, 8, (uint)l.Length); U(b, 12, 1); binding.CopyTo(b, 16);
        U(b, 48, (uint)l.Vertices); U(b, 52, (uint)l.Indices); U(b, 56, (uint)l.Draws); U(b, 60, (uint)l.Slots.Length);
        U(b, 64, Header); U(b, 68, (uint)l.Vertices * 12); U(b, 72, (uint)l.UvOffset); U(b, 76, (uint)l.Vertices * 8);
        U(b, 80, (uint)l.IndexOffset); U(b, 84, (uint)l.Indices * 4); U(b, 88, (uint)l.DrawOffset); U(b, 92, (uint)l.Draws * 32);
        U(b, 96, (uint)l.TextureOffset); U(b, 100, (uint)l.Slots.Length * 32); U(b, 104, (uint)l.TexelOffset); U(b, 108, (uint)l.TexelBytes);
        for (var i = 0; i < 3; i++) { F(b, 112 + i * 4, bounds.Min[i]); F(b, 124 + i * 4, bounds.Max[i]); }
        U(b, 136, M2Appearance.AlphaReference); U(b, 140, display); return b;
    }
    private static AppearanceManifest Manifest(AppearanceBinding binding, string bound, string id, string sha, Layout l, GeometryBounds bounds) =>
        new(id, bound, sha, l.Length, 1, Header, M2StaticGeometry.CoordinatePolicy, l.Vertices, l.Indices, l.Draws, l.Slots.Length,
            new(Header, l.Vertices * 12), new(l.UvOffset, l.Vertices * 8), new(l.IndexOffset, l.Indices * 4), new(l.DrawOffset, l.Draws * 32),
            new(l.TextureOffset, l.Slots.Length * 32), new(l.TexelOffset, (int)l.TexelBytes), bounds, M2Appearance.AlphaReference, binding.DisplayId,
            binding.GeometryBindingSha256, M2Appearance.MaterialPolicy, TexturePolicy, M2Appearance.ReaderRevision, Blp2Decoder.Revision, Revision);
    private static string ArtifactId(byte[] binding, byte[] fileHash, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("vibeminer-model-appearance-artifact-v1\0"u8);
        hash.AppendData(binding); hash.AppendData(fileHash); Span<byte> size = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)length); hash.AppendData(size);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static byte[] BindingHash(AppearanceBinding binding)
    {
        if (binding?.Textures is null || binding.DisplayFields is null || binding.DisplayId == 0 || binding.DisplayFields.Count != 5 ||
            binding.Textures.Count is < 1 or > MaximumTextures || binding.DisplayFields[0] != 0 || binding.DisplayFields.Skip(1).Any(v => v < 0))
            throw Bad("The appearance binding needs a positive display, no extended display and one to 32 texture slots.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("vibeminer-model-appearance-binding-v1\0"u8); hash.AppendData(Hex(binding.GeometryBindingSha256, 32));
        var word = new byte[4]; var sizes = new byte[16]; void Word(uint value) { U(word, 0, value); hash.AppendData(word); }
        Word(binding.DisplayId); foreach (var field in binding.DisplayFields) Word(unchecked((uint)field)); Word((uint)binding.Textures.Count);
        var seen = new HashSet<(uint, bool, bool)>(); long texels = 0;
        foreach (var t in binding.Textures)
        {
            if (t?.Source is null || t.FileDataId == 0 || !seen.Add((t.FileDataId, t.WrapU, t.WrapV)) || t.Width is < 1 or > MaximumTextureSide || t.Height is < 1 or > MaximumTextureSide ||
                t.MipLevel is < 0 or > 15 || t.BaseWidth < t.Width || t.BaseHeight < t.Height || t.BaseWidth > ushort.MaxValue || t.BaseHeight > ushort.MaxValue ||
                t.Source.VerifierRevision != "content-blte-v1" || t.Source.EncodedBytes is < 1 or > MaximumTextureSourceBytes || t.Source.DecodedBytes is < 1 or > MaximumTextureSourceBytes)
                throw Bad("A texture slot has no exact verified source, a repeated identity or a size outside the profile.");
            texels += (long)t.Width * t.Height * 4;
            Word(t.FileDataId); hash.AppendData(Hex(t.Source.ContentKey, 16)); hash.AppendData(Hex(t.Source.EncodingKey, 16)); hash.AppendData(Hex(t.Source.EncodedSha256, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(sizes, (ulong)t.Source.EncodedBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(8), (ulong)t.Source.DecodedBytes); hash.AppendData(sizes); Text(t.Source.VerifierRevision);
            Word((uint)t.Width); Word((uint)t.Height); Word((uint)t.MipLevel); Word((uint)t.BaseWidth); Word((uint)t.BaseHeight);
            hash.AppendData([EncodingCode(t.Encoding), (byte)((t.WrapU ? 1 : 0) | (t.WrapV ? 2 : 0))]);
        }
        if (texels > MaximumTexelBytes) throw Limit("The decoded texture slots exceed eight MiB.");
        foreach (var revision in new[] { M2Appearance.ReaderRevision, M2Appearance.MaterialPolicy, TexturePolicy, Blp2Decoder.Revision, M2StaticGeometry.CoordinatePolicy, Revision }) Text(revision);
        Span<byte> version = stackalloc byte[2]; W(version, 0, 1); hash.AppendData(version);
        return hash.GetHashAndReset();
        void Text(string value)
        {
            Span<byte> length = stackalloc byte[2]; W(length, 0, (ushort)value.Length); hash.AppendData(length); hash.AppendData(Encoding.ASCII.GetBytes(value));
        }
    }
    private sealed record Layout(int Vertices, int Indices, int Draws, long[] Slots)
    {
        internal int UvOffset => Header + Vertices * 12;
        internal int IndexOffset => UvOffset + Vertices * 8;
        internal int DrawOffset => IndexOffset + Indices * 4;
        internal int TextureOffset => DrawOffset + Draws * 32;
        internal int TexelOffset => TextureOffset + Slots.Length * 32;
        internal long TexelBytes => Slots.Sum();
        internal long Length => TexelOffset + TexelBytes;
    }
}
