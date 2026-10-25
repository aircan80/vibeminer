using System.Buffers.Binary;
using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

/// <summary>Original bounded BLP2 image decoder. Source authentication belongs to the caller.</summary>
public static partial class Blp2Decoder
{
    public const string Revision = "blp2-rgba8-v2";
    private const int HeaderBytes = 148;
    private const int PaletteBytes = 1024;
    private static DiscoveryException Error(string code, string message) => new("BLP_" + code, message);

    public static async Task<Blp2Image> DecodeAsync(Stream source, int mipLevel = 0, Blp2Limits? limits = null,
        CancellationToken token = default) => (await DecodeCoreAsync(source, mipLevel, 0, limits, token))!;

    /// <summary>Validates the container header and the descriptor structure as decoding would, and returns the declared
    /// levels with their storage, without reading pixels. A level's length, bounds and overlap are checked when it is decoded.</summary>
    public static async Task<IReadOnlyList<Blp2Mip>> ReadMipsAsync(Stream source, Blp2Limits? limits = null, CancellationToken token = default)
    {
        var declared = new List<Blp2Mip>(); await DecodeCoreAsync(source, 0, 0, limits, token, declared); return declared.AsReadOnly();
    }

    /// <summary>Decodes the largest declared mip whose width and height are both at most <paramref name="maxSide"/>,
    /// skipping compact levels. No declared level fitting is <c>BLP_MIP_UNAVAILABLE</c>; nothing is resized.</summary>
    public static async Task<Blp2Image> DecodeLargestWithinAsync(Stream source, int maxSide, Blp2Limits? limits = null,
        CancellationToken token = default)
    {
        if (maxSide < 1) throw new ArgumentOutOfRangeException(nameof(maxSide));
        return (await DecodeCoreAsync(source, 0, maxSide, limits, token))!;
    }

    // With `declared`, stops after the descriptor checks and returns null.
    private static async Task<Blp2Image?> DecodeCoreAsync(Stream source, int mipLevel, int maxSide, Blp2Limits? limits,
        CancellationToken token, List<Blp2Mip>? declared = null)
    {
        ArgumentNullException.ThrowIfNull(source); token.ThrowIfCancellationRequested(); limits ??= new();
        if (limits.MaxSourceBytes < HeaderBytes || limits.MaxDimension < 1 || limits.MaxPixels < 1
            || limits.MaxMipBytes < 1 || limits.MaxRgbaBytes < 4) throw new ArgumentOutOfRangeException(nameof(limits));
        if (mipLevel is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("A readable seekable caller-owned source is required.", nameof(source));
        var sourceLength = source.Length;
        if (sourceLength > limits.MaxSourceBytes) throw Error("LIMIT", "BLP source exceeds its byte bound.");
        if (sourceLength < HeaderBytes) throw Error("INVALID", "BLP2 core header is truncated.");
        var header = await ReadAtAsync(source, 0, HeaderBytes, token);
        if (!header.AsSpan(0, 4).SequenceEqual("BLP2"u8)) throw Error("UNSUPPORTED", "Only BLP2 containers are supported.");
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at, 4));
        if (U(4) != 1) throw Error("UNSUPPORTED", "Only BLP2 direct/palette/block content is supported; JPEG is unsupported.");
        var alphaDepth = header[9]; var alphaEncoding = header[10];
        var encoding = SelectEncoding(header[8], alphaDepth, alphaEncoding);
        if (!IsMipFlags(header[11])) throw Error("UNSUPPORTED", "BLP mip byte 0x" + header[11].ToString("x2") + " is not supported; only 0x00, 0x01, 0x10 and 0x11 are.");
        var width = Dimension(U(12), limits); var height = Dimension(U(16), limits);
        var minimumOffset = encoding == Blp2Encoding.Paletted ? HeaderBytes + PaletteBytes : HeaderBytes;
        if (sourceLength < minimumOffset) throw Error("INVALID", "BLP2 palette is truncated.");
        var mips = new List<Blp2Mip>(); var absent = false;
        for (var level = 0; level < 16; level++)
        {
            token.ThrowIfCancellationRequested(); var offset = U(20 + level * 4); var count = U(84 + level * 4);
            if (offset == 0 && count == 0) { absent = true; continue; }
            if (offset == 0 || count == 0 || absent) throw Error("INVALID", "BLP mip descriptors must form a nonempty contiguous prefix with paired offsets and lengths.");
            if (level > 0 && !AllowsMipLevels(header[11])) throw Error("INVALID", "The BLP mip flag excludes additional declared levels.");
            if (level > 0 && Math.Max(width, height) >> (level - 1) <= 1)
                throw Error("INVALID", "The BLP declares mip levels beyond its terminal one-pixel level.");
            var mipWidth = Math.Max(1, width >> level); var mipHeight = Math.Max(1, height >> level);
            mips.Add(new(level, mipWidth, mipHeight, offset, count, Storage(count, mipWidth, mipHeight, encoding, alphaDepth)));
        }
        if (mips.Count == 0) throw Error("INVALID", "The BLP source has no declared top-level mip.");
        if (declared is not null) { declared.AddRange(mips); return null; }
        if (maxSide > 0)
        {
            mipLevel = mips.FindIndex(mip => mip.Width <= maxSide && mip.Height <= maxSide && mip.Storage != Blp2MipStorage.Compact);
            if (mipLevel < 0) throw Error("MIP_UNAVAILABLE", "No declared mip fits the requested size; no level was resized or substituted.");
        }
        if (mipLevel >= mips.Count) throw Error("MIP_UNAVAILABLE", "The requested mip is not present; no alternative level was substituted.");
        // Only the decoded level is held to its exact geometry, the source bounds and the other declared ranges.
        var selected = mips[mipLevel];
        if (selected.Storage == Blp2MipStorage.Compact)
            throw Error("UNSUPPORTED", "This BLP mip is stored with width × height sizing, short of its 4×4 blocks; it is not decoded.");
        if (selected.Storage != Blp2MipStorage.Exact) throw Error("INVALID", "The BLP mip length differs from its exact pixel/block geometry.");
        if (selected.Offset < minimumOffset || selected.Offset > sourceLength || selected.ByteLength > sourceLength - selected.Offset)
            throw Error("INVALID", "The BLP mip overlaps metadata or lies outside the source.");
        if (mips.Any(mip => mip.Level != selected.Level && mip.Offset < selected.Offset + selected.ByteLength && selected.Offset < mip.Offset + mip.ByteLength))
            throw Error("INVALID", "The BLP mip overlaps another declared mip.");
        var pixels = selected.Width * (long)selected.Height;
        if (pixels > limits.MaxPixels || pixels > limits.MaxRgbaBytes / 4 || pixels > int.MaxValue / 4
            || selected.ByteLength > limits.MaxMipBytes || selected.ByteLength > int.MaxValue)
            throw Error("LIMIT", "The selected BLP mip exceeds decoded pixel/working-byte bounds.");
        var palette = encoding == Blp2Encoding.Paletted ? await ReadAtAsync(source, HeaderBytes, PaletteBytes, token) : Array.Empty<byte>();
        var payload = await ReadAtAsync(source, selected.Offset, (int)selected.ByteLength, token);
        var rgba = new byte[(int)pixels * 4];
        if (encoding == Blp2Encoding.Paletted) DecodePalette(payload, palette, rgba, selected.Width, selected.Height, alphaDepth, token);
        else if (encoding == Blp2Encoding.Bgra8) DecodeBgra(payload, rgba, selected.Width, selected.Height, token);
        else DecodeBlocks(payload, rgba, selected.Width, selected.Height, encoding, alphaDepth, token);
        token.ThrowIfCancellationRequested(); source.Position = sourceLength;
        if (await source.ReadAsync(new byte[1], token) != 0 || source.Length != sourceLength)
            throw Error("INVALID", "The BLP source changed length during decoding.");
        return new(selected.Width, selected.Height, checked(selected.Width * 4), rgba, encoding,
            alphaDepth, alphaEncoding, mipLevel, width, height, mips.AsReadOnly(), header[11]);
    }

    private static int Dimension(uint value, Blp2Limits limits)
    { if (value == 0) throw Error("INVALID", "BLP dimensions must be positive."); if (value > limits.MaxDimension) throw Error("LIMIT", "BLP dimensions exceed policy."); return (int)value; }
    /// <summary>Mip byte (header offset 11): bit 0 allows levels after the first; bit 4 is kept but changes nothing (D100).</summary>
    public static bool IsMipFlags(int flags) => flags is 0x00 or 0x01 or 0x10 or 0x11;
    public static bool AllowsMipLevels(int flags) => (flags & 0x01) != 0;
    /// <summary>Whether a decoded encoding was read from one of the supported header profiles (see docs/formats/blp.md).</summary>
    public static bool IsSupported(Blp2Encoding encoding, int alphaDepth, int alphaEncoding) =>
        alphaDepth is >= 0 and <= 255 && alphaEncoding is >= 0 and <= 255 && Profile(encoding switch
        { Blp2Encoding.Paletted => 1, Blp2Encoding.Bgra8 => 3, _ => 2 }, (byte)alphaDepth, (byte)alphaEncoding) == encoding;
    private static Blp2Encoding SelectEncoding(byte encoding, byte depth, byte alphaEncoding) => Profile(encoding, depth, alphaEncoding)
        ?? throw Error("UNSUPPORTED", "BLP colour encoding " + encoding + " with alpha depth " + depth + " and alpha encoding " + alphaEncoding + " is not supported.");
    // Only observed profiles. Palette and raw BGRA carry alpha encoding 0 or 8; 8 changes nothing there. BC3's alpha is in its
    // blocks, so its depth byte is not read; 72 is the one other depth seen (D100).
    private static Blp2Encoding? Profile(byte encoding, byte depth, byte alphaEncoding) => encoding switch
    {
        1 when depth is 0 or 1 or 4 or 8 && alphaEncoding == 0 => Blp2Encoding.Paletted,
        1 when depth == 0 && alphaEncoding == 8 => Blp2Encoding.Paletted,
        2 when depth is 0 or 1 && alphaEncoding == 0 => Blp2Encoding.Bc1,
        2 when depth is 4 or 8 && alphaEncoding == 1 => Blp2Encoding.Bc2,
        2 when depth is 8 or 72 && alphaEncoding == 7 => Blp2Encoding.Bc3,
        3 when depth == 8 && alphaEncoding == 0 => Blp2Encoding.Bgra8,
        3 when depth == 1 && alphaEncoding == 8 => Blp2Encoding.Bgra8,
        _ => null
    };
    private static Blp2MipStorage Storage(uint count, int width, int height, Blp2Encoding encoding, int alphaDepth)
    {
        var pixels = width * (long)height;
        try
        {
            var exact = encoding switch
            {
                Blp2Encoding.Paletted => checked(pixels + (checked(pixels * alphaDepth) + 7) / 8),
                Blp2Encoding.Bgra8 => checked(pixels * 4),
                _ => checked(((width + 3L) / 4) * ((height + 3L) / 4) * (encoding == Blp2Encoding.Bc1 ? 8 : 16))
            };
            if (count == exact) return Blp2MipStorage.Exact;
            // Blizzard sizes block levels as width × height at 4 (BC1) or 8 (BC2/BC3) bits per pixel, at least one block.
            // With a side under 4 px that is fewer bytes than the level's blocks.
            if (encoding is Blp2Encoding.Bc1 or Blp2Encoding.Bc2 or Blp2Encoding.Bc3)
            {
                var block = encoding == Blp2Encoding.Bc1 ? 8 : 16;
                var compact = Math.Max(block, encoding == Blp2Encoding.Bc1 ? pixels / 2 : pixels);
                if (count == compact && compact < exact) return Blp2MipStorage.Compact;
            }
            return Blp2MipStorage.Unrecognized;
        }
        catch (OverflowException) { throw Error("LIMIT", "BLP mip geometry exceeds the supported integer byte domain."); }
    }
    private static async Task<byte[]> ReadAtAsync(Stream source, long offset, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (offset > source.Length || count > source.Length - offset) throw Error("INVALID", "BLP bytes are truncated.");
        source.Position = offset; var bytes = new byte[count];
        try
        {
            for (var done = 0; done < count;)
            { token.ThrowIfCancellationRequested(); var take = Math.Min(65_536, count - done); await source.ReadExactlyAsync(bytes.AsMemory(done, take), token); done += take; }
        }
        catch (EndOfStreamException) { throw Error("INVALID", "BLP bytes ended unexpectedly."); }
        return bytes;
    }
    private static void DecodePalette(byte[] payload, byte[] palette, byte[] rgba, int width, int height, int depth, CancellationToken token)
    {
        var pixels = width * height;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x; var color = payload[index] * 4; var target = index * 4;
                rgba[target] = palette[color + 2]; rgba[target + 1] = palette[color + 1]; rgba[target + 2] = palette[color];
                rgba[target + 3] = depth switch
                { 0 => 255, 1 => (byte)(((payload[pixels + index / 8] >> (index % 8)) & 1) * 255), 4 => (byte)(((payload[pixels + index / 2] >> ((index % 2) * 4)) & 15) * 17), _ => payload[pixels + index] };
            }
        }
    }
    private static void DecodeBgra(byte[] payload, byte[] rgba, int width, int height, CancellationToken token)
    {
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            { var at = (y * width + x) * 4; rgba[at] = payload[at + 2]; rgba[at + 1] = payload[at + 1]; rgba[at + 2] = payload[at]; rgba[at + 3] = payload[at + 3]; }
        }
    }
}
