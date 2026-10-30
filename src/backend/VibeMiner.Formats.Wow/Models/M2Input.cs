using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Formats.Wow;

internal readonly record struct M2Span(string Name, long Offset, int Count, int Stride);
internal delegate void M2RecordConsumer(ReadOnlySpan<byte> row, int ordinal);
internal sealed class M2Input
{
    private readonly Stream source;
    private readonly CancellationToken token;
    internal long Length { get; }
    internal M2Input(Stream source, M2Limits limits, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); CheckLimits(limits); token.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || source.Position != 0)
            throw new ArgumentException("A readable seekable caller-owned stream starting at zero is required.", nameof(source));
        if (source is IBlteProjectionSource projection && projection.UnavailableRanges.Count != 0)
            throw Error("UNSUPPORTED", "The initial model profile requires a complete decoded source, not protected ranges.");
        this.source = source; this.token = token; Length = source.Length;
        if (Length > limits.MaxSourceBytes) throw Error("LIMIT", "The model source exceeds its byte budget.");
        if (Length < 4) throw Error("INVALID", "The model magic is truncated.");
    }
    internal static DiscoveryException Error(string code, string message) => new("MODEL_" + code, message);
    /// <summary>A count over its cap names the source's count and the cap in plain words, as the size refusal does (#114).</summary>
    internal static DiscoveryException CountLimit(string what, long maximum, string unit = "") =>
        Error("LIMIT", what + "; geometry reads at most " + N(maximum) + unit + ".");
    internal static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    // "collisionTriangles" reads "collision triangles"; a "SKIN ..." name belongs to the SKIN file, every other to the model.
    private static string SpanLimit(string name, uint count, int stride)
    {
        var skin = name.StartsWith("SKIN ", StringComparison.Ordinal);
        var words = string.Concat((skin ? name[5..] : name).Select(c => char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        var owner = skin ? "The SKIN file" : "The model";
        // Names and filenames are byte strings; every other span counts records.
        return stride == 1 ? $"{owner}'s {words} is {N(count)} bytes"
            : $"{owner} has {N(count)} {words}{(words.EndsWith("lookup", StringComparison.Ordinal) ? " entries" : "")}";
    }
    internal static void CheckLimits(M2Limits limits)
    {
        if (limits.MaxSourceBytes is < 4 or > (64L << 20) || limits.MaxOutputBytes is < 1 or > (128L << 20)
            || limits.MaxVertices is < 1 or > 1_000_000 || limits.MaxVertexLookup is < 1 or > 65_536
            || limits.MaxTriangleIndices is < 3 or > 3_000_000 || limits.MaxSections is < 1 or > 256
            || limits.MaxMaterials is < 1 or > 256 || limits.MaxBatches is < 1 or > 512 || limits.MaxTextures is < 1 or > 32
            || limits.MaxSkinProfiles is < 1 or > 32 || limits.MaxChunks is < 1 or > 128
            || limits.MaxNameBytes is < 1 or > 4096 || limits.MaxMetadataEntries is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(limits), "Model limits must fit the finite decoder ceilings.");
    }
    internal async Task<byte[]> AtAsync(long offset, int count)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || count is < 0 or > 65_536 || offset > Length || count > Length - offset)
            throw Error("INVALID", "A model read lies outside its source.");
        var bytes = new byte[count]; source.Position = offset;
        try { await source.ReadExactlyAsync(bytes, token); }
        catch (EndOfStreamException) { throw Error("INVALID", "A model structure ended unexpectedly."); }
        return bytes;
    }
    internal M2Span Span(string name, uint count, uint offset, int stride, int maximum, long origin, long bytes, int header)
    {
        if (count > maximum) throw CountLimit(SpanLimit(name, count, stride), maximum, stride == 1 ? " bytes" : "");
        if (count != 0 && (offset < header || offset > bytes || (long)count * Math.Max(1, stride) > bytes - offset))
            throw Error("INVALID", $"The {name} span lies outside its model section or overlaps its header.");
        return new(name, origin + offset, (int)count, stride);
    }
    internal async Task RecordsAsync(M2Span span, M2RecordConsumer consume)
    {
        token.ThrowIfCancellationRequested(); if (span.Count == 0) return;
        if (span.Stride is < 1 or > 256) throw new InvalidOperationException("Only fixed bounded model records may be read.");
        var batch = Math.Max(1, 65_536 / span.Stride); var buffer = new byte[Math.Min(batch, span.Count) * span.Stride];
        source.Position = span.Offset;
        for (var first = 0; first < span.Count;)
        {
            token.ThrowIfCancellationRequested(); var rows = Math.Min(batch, span.Count - first);
            try { await source.ReadExactlyAsync(buffer.AsMemory(0, rows * span.Stride), token); }
            catch (EndOfStreamException) { throw Error("INVALID", $"The {span.Name} records ended unexpectedly."); }
            for (var row = 0; row < rows; row++)
            { token.ThrowIfCancellationRequested(); consume(buffer.AsSpan(row * span.Stride, span.Stride), first + row); }
            first += rows; await Task.Yield();
        }
    }
    internal async Task FinishAsync()
    {
        token.ThrowIfCancellationRequested(); if (source.Length != Length) throw Error("INVALID", "The source length changed during decoding.");
        source.Position = Length;
        if (await source.ReadAsync(new byte[1], token) != 0) throw Error("INVALID", "The source has data beyond its declared length.");
    }
    internal static ushort U16(ReadOnlySpan<byte> data, int at = 0) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
    internal static short I16(ReadOnlySpan<byte> data, int at = 0) => BinaryPrimitives.ReadInt16LittleEndian(data[at..]);
    internal static uint U32(ReadOnlySpan<byte> data, int at = 0) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    internal static int I32(ReadOnlySpan<byte> data, int at = 0) => BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
    internal static float F(ReadOnlySpan<byte> data, int at = 0)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(data[at..]);
        if (!float.IsFinite(value)) throw Error("INVALID", "A model geometry value is not finite."); return value;
    }
    internal static Vector3 V3(ReadOnlySpan<byte> data, int at = 0) => new(F(data, at), F(data, at + 4), F(data, at + 8));
    internal static Vector2 V2(ReadOnlySpan<byte> data, int at = 0) => new(F(data, at), F(data, at + 4));
    internal static M2Bounds Bounds(ReadOnlySpan<byte> data, int at, bool emptyAllowed = false)
    {
        var min = V3(data, at); var max = V3(data, at + 12); var radius = F(data, at + 24);
        if (radius < 0) throw Error("INVALID", "A declared model bound has a negative radius.");
        // The empty box: minimum above maximum on every axis, radius 0. Retail writes it for a model with no collision mesh (#98).
        if (emptyAllowed && min.X > max.X && min.Y > max.Y && min.Z > max.Z && radius == 0) return new(min, max, radius);
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw Error("INVALID", emptyAllowed ? "The declared collision bound is inverted but not empty." : "A declared model bound is inverted.");
        return new(min, max, radius);
    }
}
internal sealed class M2Budget(long maximum, long initial = 0)
{
    internal long Used { get; private set; } = initial;
    internal void Add(long count, int bytes)
    {
        if (count < 0 || bytes < 0 || Used > maximum || count > (maximum - Used) / Math.Max(1, bytes))
            throw M2Input.Error("LIMIT", "The combined retained model/SKIN/assembly output exceeds its byte budget.");
        Used += count * bytes;
    }
}
