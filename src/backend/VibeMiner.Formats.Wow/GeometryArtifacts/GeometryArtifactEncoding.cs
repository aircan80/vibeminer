using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

internal static class GeometryArtifactEncoding
{
    internal const int BufferBytes = 65536, HeaderBytes = 128, MaximumBytes = 2097152;
    // Logical payload allowance includes the full48-byte value returned by the vertex indexer.
    // These are conservative work quanta, not measured physical memory traffic; I/O is independently64KiB.
    internal const int PositionWriteVertices = BufferBytes / 60;
    // Reserve64 bytes for the section value indexer plus12 index bytes and24 output bytes per triangle.
    internal const int EdgeWriteBytes = BufferBytes / 100 * 24;
    internal const int EdgeVerifyBytes = BufferBytes / 60 * 24;
    internal const int MaximumVertices = 65536, MaximumIndices = 131070, MaximumSections = 256;
    internal const string ArtifactRevision = "model-geometry-vmg1-v1", EdgePolicy = "all-sections-triangle-edges-v1";
    internal const string Scope = "unfiltered-declared-sections";
    internal static DiscoveryException Invalid(string detail) => new("GEOMETRY_ARTIFACT_INVALID", detail);
    internal static DiscoveryException Limit(string detail) => new("GEOMETRY_ARTIFACT_LIMIT", detail);
    internal static DiscoveryException Unsupported(string detail) => new("GEOMETRY_ARTIFACT_UNSUPPORTED", detail);
    internal static uint U(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    internal static ushort W(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    internal static float F(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
    internal static void U(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
    internal static void W(Span<byte> bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[at..], value);
    internal static void F(Span<byte> bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[at..], value);
    internal static Vector3 V(ReadOnlySpan<byte> bytes, int at) => new(F(bytes, at), F(bytes, at + 4), F(bytes, at + 8));
    internal static float Zero(float f) => f == 0 ? 0 : f;
    internal static Vector3 Canonical(Vector3 v) => new(Zero(v.X), Zero(v.Y), Zero(v.Z));
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    internal static bool Same(GeometryBounds a, GeometryBounds b) => Enumerable.Range(0, 3).All(i => Same(a.Min[i], b.Min[i]) && Same(a.Max[i], b.Max[i]));
    internal static byte[] Hex(string? value, int length)
    {
        if (value is null || value.Length != length * 2 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("Artifact identities require canonical lowercase hexadecimal values.");
        return Convert.FromHexString(value);
    }
    internal static byte[] BindingHash(GeometrySourceBinding binding)
    {
        if (binding is null || binding.Model is null || binding.Skin is null || binding.ModelFileDataId == 0 || binding.SkinFileDataId == 0 || binding.MainSkinOrdinal != 0)
            throw Invalid("The geometry binding requires two positive exact source identities and main ordinal zero.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("vibeminer-model-geometry-binding-v1\0"u8);
        Source(binding.Model); Source(binding.Skin);
        Span<byte> ids = stackalloc byte[12]; U(ids, 0, binding.ModelFileDataId); U(ids, 4, binding.SkinFileDataId); U(ids, 8, 0); hash.AppendData(ids);
        Text(M2ModelReader.Revision); Text(M2GeometrySkinReader.Revision); Text(M2GeometrySkin.InterpretationPolicy);
        Text(M2StaticGeometry.CoordinatePolicy); Text(ArtifactRevision); Text(EdgePolicy);
        Span<byte> codes = stackalloc byte[10]; U(codes, 0, 1); U(codes, 4, 1); W(codes, 8, 1); hash.AppendData(codes);
        return hash.GetHashAndReset();
        void Source(GeometryOriginalSource source)
        {
            hash.AppendData(Hex(source.ContentKey, 16)); hash.AppendData(Hex(source.EncodingKey, 16)); hash.AppendData(Hex(source.EncodedSha256, 32));
            if (source.EncodedBytes <= 0 || source.DecodedBytes <= 0 || source.VerifierRevision != "content-blte-v1")
                throw Invalid("The source binding requires positive lengths and the original full verifier revision.");
            if (source.EncodedBytes > 4L << 20 || source.DecodedBytes > 8L << 20) throw Limit("The source exceeds the initial geometry delivery profile.");
            Span<byte> sizes = stackalloc byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(sizes, (ulong)source.EncodedBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(sizes[8..], (ulong)source.DecodedBytes); hash.AppendData(sizes); Text(source.VerifierRevision);
        }
        void Text(string value)
        {
            if (value.Length is < 1 or > 128 || value.Any(c => c is < ' ' or > '~')) throw Invalid("The binding revision is not bounded printable ASCII.");
            Span<byte> length = stackalloc byte[2]; W(length, 0, (ushort)value.Length); hash.AppendData(length); hash.AppendData(Encoding.ASCII.GetBytes(value));
        }
    }
    internal static string ArtifactId(byte[] binding, byte[] fileHash, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("vibeminer-model-geometry-artifact-v1\0"u8);
        hash.AppendData(binding); hash.AppendData(fileHash); Span<byte> size = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)length); hash.AppendData(size);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static GeometryManifest Manifest(GeometrySourceBinding binding, string bindingHash, string id, string sha, Header h) =>
        new(id, bindingHash, sha, h.Length, 1, HeaderBytes, Scope, M2StaticGeometry.CoordinatePolicy,
            new("notEvaluated", "notEvaluated", "notEvaluated", "notEvaluated"), h.Vertices, h.Lookup, h.GlobalIndices,
            h.SectionIndices, h.Lines, h.Sections, new(HeaderBytes, h.Vertices * 12), new(h.LineOffset, h.Lines * 4),
            new(h.DirectoryOffset, h.Sections * 32), h.Bounds, binding.ModelFileDataId, binding.SkinFileDataId, 0,
            M2ModelReader.Revision, M2GeometrySkinReader.Revision, M2GeometrySkin.InterpretationPolicy, ArtifactRevision, EdgePolicy);
    internal sealed record Header(int Vertices, int Lookup, int GlobalIndices, int SectionIndices, int Sections, GeometryBounds Bounds)
    {
        internal int Lines => SectionIndices * 2;
        internal int LineOffset => HeaderBytes + Vertices * 12;
        internal int DirectoryOffset => LineOffset + Lines * 4;
        internal int Length => DirectoryOffset + Sections * 32;
    }
    internal static void Counts(int vertices, int lookup, int global, int summed, int sections)
    {
        if (vertices < 1 || lookup < 1 || global < 1 || summed < 1 || sections < 1 || global % 3 != 0 || summed % 3 != 0)
            throw Invalid("The geometry counts are empty or not triangle-aligned.");
        if (vertices > MaximumVertices || lookup > MaximumVertices || global > MaximumIndices || summed > MaximumIndices || sections > MaximumSections)
            throw Limit("Geometry exceeds the finite initial artifact profile.");
    }
    internal static void BoundsValid(GeometryBounds bounds)
    {
        if (bounds is null) throw Invalid("Bounds are absent.");
        for (var i = 0; i < 3; i++)
            if (!float.IsFinite(bounds.Min[i]) || !float.IsFinite(bounds.Max[i]) || bounds.Min[i] > bounds.Max[i]
                || !Same(bounds.Min[i], Zero(bounds.Min[i])) || !Same(bounds.Max[i], Zero(bounds.Max[i])))
                throw Invalid("Bounds must be finite, ordered and have canonical zero bits.");
    }
}
