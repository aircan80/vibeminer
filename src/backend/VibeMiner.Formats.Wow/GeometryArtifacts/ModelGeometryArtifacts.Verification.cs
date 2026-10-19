using System.Numerics;
using System.Security.Cryptography;
using static VibeMiner.Formats.Wow.GeometryArtifactEncoding;

namespace VibeMiner.Formats.Wow;

public static partial class ModelGeometryArtifacts
{
    /// <summary>Validates app bytes against an engine-owned descriptor; grants no source/current-context authority.</summary>
    public static Task VerifyAsync(Stream source, GeometryArtifact expectedDescriptor, CancellationToken token = default) =>
        VerifyAsync(source, expectedDescriptor, GeometryArtifactWork.Unbudgeted, token);

    public static async Task VerifyAsync(Stream source, GeometryArtifact expectedDescriptor,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        Header h; byte[] bindingHash, expectedHash;
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            (h, bindingHash, expectedHash) = ValidateDescriptor(expectedDescriptor);
        await Task.Yield(); token.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || source.Position != 0 || source.Length != h.Length)
            throw Invalid("The caller-owned artifact input must be readable, seekable, complete and at position zero.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes]; var header = new byte[HeaderBytes];
        await ReadAsync(header);
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            if (!header.AsSpan().SequenceEqual(HeaderBytesFor(h, bindingHash))) throw Invalid("VMG1 header differs from the exact known descriptor/framing.");
        await Task.Yield();
        // The sole retained payload is a bounded positions buffer. No evicting cache or vertex rereads.
        var positions = new byte[h.Vertices * 12];
        for (var at = 0; at < positions.Length;)
        { var count = Math.Min(BufferBytes, positions.Length - at); await ReadAsync(positions.AsMemory(at, count)); at += count; }
        for (var first = 0; first < h.Vertices;)
        {
            var count = Math.Min(BufferBytes / 12, h.Vertices - first);
            using (await GeometryArtifactWork.EnterAsync(budget, token))
                for (var i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (!Finite(V(positions, (first + i) * 12))) throw Invalid("An artifact position is not finite.");
                }
            await Task.Yield(); first += count;
        }
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var first = 0; first < h.Lines;)
        {
            var count = Math.Min(BufferBytes / 24 * 6, h.Lines - first); await ReadAsync(buffer.AsMemory(0, count * 4));
            for (var start = 0; start < count * 4;)
            {
                var end = Math.Min(count * 4, start + EdgeVerifyBytes);
                using (await GeometryArtifactWork.EnterAsync(budget, token))
                {
                    for (var at = start; at < end; at += 24)
                    {
                        if ((at / 24 & 511) == 0) token.ThrowIfCancellationRequested();
                        var a = U(buffer, at); var b = U(buffer, at + 4); var c = U(buffer, at + 12);
                        if (a >= h.Vertices || b >= h.Vertices || c >= h.Vertices || U(buffer, at + 8) != b || U(buffer, at + 16) != c || U(buffer, at + 20) != a)
                            throw Invalid("An app edge group is not the exact bounded triangle-pair pattern.");
                        Include(a); Include(b); Include(c);
                    }
                }
                await Task.Yield(); start = end;
            }
            first += count;
        }
        await ReadAsync(buffer.AsMemory(0, h.Sections * 32)); long cumulative = 0;
        using (await GeometryArtifactWork.EnterAsync(budget, token))
        {
            for (var i = 0; i < h.Sections; i++)
            {
                token.ThrowIfCancellationRequested(); var at = i * 32;
                var vertexStart = U(buffer, at + 8); var vertexCount = U(buffer, at + 12); var start = U(buffer, at + 16); var count = U(buffer, at + 20);
                if (U(buffer, at) != i || vertexStart > ushort.MaxValue || vertexCount > ushort.MaxValue || count > ushort.MaxValue
                    || start >> 16 != W(buffer, at + 6) || start % 3 != 0 || count % 3 != 0
                    || (long)vertexStart + vertexCount > h.Lookup || (long)start + count > h.GlobalIndices
                    || count != 0 && vertexCount == 0 || U(buffer, at + 24) != cumulative || U(buffer, at + 28) != (long)count * 2)
                    throw Invalid("A directory record violates native section provenance or exact line partitioning.");
                cumulative += (long)count * 2;
            }
            if (cumulative != h.Lines || !Same(new GeometryBounds(Canonical(min), Canonical(max)), h.Bounds))
                throw Invalid("The section partition or tight referenced bounds differ from the artifact declaration.");
        }
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (source.Position != h.Length || source.Length != h.Length) throw Invalid("Artifact length changed during verification.");
        var trailing = await source.ReadAsync(buffer.AsMemory(0, 1), token); token.ThrowIfCancellationRequested();
        if (trailing != 0 || source.Length != h.Length) throw Invalid("The artifact has trailing bytes or changed geometry.");
        using (await GeometryArtifactWork.EnterAsync(budget, token))
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash)) throw Invalid("The complete artifact SHA256 does not match its engine descriptor.");

        void Include(uint index)
        { var p = V(positions, (int)index * 12); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        async ValueTask ReadAsync(Memory<byte> bytes)
        {
            token.ThrowIfCancellationRequested();
            try { await source.ReadExactlyAsync(bytes, token); }
            catch (EndOfStreamException) { throw Invalid("The VMG1 artifact is truncated."); }
            using (await GeometryArtifactWork.EnterAsync(budget, token)) hash.AppendData(bytes.Span);
            await Task.Yield(); token.ThrowIfCancellationRequested();
        }
    }
    private static (Header Header, byte[] BindingHash, byte[] ExpectedHash) ValidateDescriptor(GeometryArtifact descriptor)
    {
        if (descriptor is null || descriptor.Manifest is null) throw Invalid("An engine artifact descriptor is required.");
        var bindingHash = BindingHash(descriptor.Binding); var hexBinding = Convert.ToHexStringLower(bindingHash);
        Hex(descriptor.BindingSha256, 32); Hex(descriptor.ArtifactId, 32); var fileHash = Hex(descriptor.Sha256, 32);
        if (descriptor.ByteLength > MaximumBytes) throw Limit("The artifact exceeds two MiB.");
        var m = descriptor.Manifest;
        Counts(m.VertexCount, m.VertexLookupCount, m.SourceTriangleIndexCount, m.SectionIndexCount, m.SectionCount); BoundsValid(m.Bounds);
        var h = new Header(m.VertexCount, m.VertexLookupCount, m.SourceTriangleIndexCount, m.SectionIndexCount, m.SectionCount, m.Bounds);
        if (descriptor.BindingSha256 != hexBinding || descriptor.ByteLength != h.Length || descriptor.ArtifactId != ArtifactId(bindingHash, fileHash, h.Length)
            || m != Manifest(descriptor.Binding, hexBinding, descriptor.ArtifactId, descriptor.Sha256, h))
            throw Invalid("The canonical artifact descriptor, revisions or manifest fields do not match.");
        return (h, bindingHash, fileHash);
    }
}
