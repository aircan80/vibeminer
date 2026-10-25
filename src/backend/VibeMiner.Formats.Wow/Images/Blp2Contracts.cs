namespace VibeMiner.Formats.Wow;

/// <summary>Byte/pixel allocation limits apply to the selected mip; source/dimension limits apply to the container.</summary>
public sealed record Blp2Limits(long MaxSourceBytes = 64L << 20, int MaxDimension = 16_384,
    long MaxPixels = 16_777_216, long MaxMipBytes = 64L << 20, long MaxRgbaBytes = 64L << 20);
public enum Blp2Encoding { Paletted, Bc1, Bc2, Bc3, Bgra8 }
/// <summary>How a declared level's length relates to its geometry. Only <see cref="Exact"/> levels are decoded.</summary>
public enum Blp2MipStorage
{
    /// <summary>Every pixel, or every 4×4 block, of the level.</summary>
    Exact,
    /// <summary>Blizzard's width × height sizing of a block level with a side under 4 px: fewer bytes than its blocks need. Never decoded.</summary>
    Compact,
    /// <summary>Any other length. Refused if the level is decoded.</summary>
    Unrecognized
}
public sealed record Blp2Mip(int Level, int Width, int Height, long Offset, long ByteLength, Blp2MipStorage Storage = Blp2MipStorage.Exact);
/// <summary>Owned tightly packed RGBA8, top-left origin, straight alpha; no color-space conversion.</summary>
public sealed record Blp2Image(int Width, int Height, int RowStride, byte[] Rgba, Blp2Encoding Encoding,
    byte AlphaDepth, byte AlphaEncoding, int MipLevel, int BaseWidth, int BaseHeight, IReadOnlyList<Blp2Mip> Mips, int MipFlags = 0);
