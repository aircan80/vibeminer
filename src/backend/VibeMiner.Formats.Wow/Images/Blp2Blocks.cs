using System.Buffers.Binary;

namespace VibeMiner.Formats.Wow;

public static partial class Blp2Decoder
{
    // Deterministic CPU RGBA8 policy: replicate RGB565 endpoint bits to eight bits,
    // then use integer division (floor) for interpolated channels. No premultiplication.
    private static void DecodeBlocks(byte[] payload, byte[] rgba, int width, int height,
        Blp2Encoding encoding, int alphaDepth, CancellationToken token)
    {
        var blockBytes = encoding == Blp2Encoding.Bc1 ? 8 : 16; var blocksWide = (width + 3) / 4; var blocksHigh = (height + 3) / 4;
        Span<byte> colors = stackalloc byte[16]; Span<byte> alphas = stackalloc byte[8];
        for (var by = 0; by < blocksHigh; by++)
        {
            token.ThrowIfCancellationRequested();
            for (var bx = 0; bx < blocksWide; bx++)
            {
                token.ThrowIfCancellationRequested(); var at = (by * blocksWide + bx) * blockBytes;
                var colorAt = at + (encoding == Blp2Encoding.Bc1 ? 0 : 8);
                var c0 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(colorAt, 2)); var c1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(colorAt + 2, 2));
                Color565(c0, colors[..4]); Color565(c1, colors.Slice(4, 4));
                var fourColors = encoding != Blp2Encoding.Bc1 || c0 > c1;
                for (var channel = 0; channel < 3; channel++)
                {
                    colors[8 + channel] = (byte)(fourColors ? (2 * colors[channel] + colors[4 + channel]) / 3 : (colors[channel] + colors[4 + channel]) / 2);
                    colors[12 + channel] = fourColors ? (byte)((colors[channel] + 2 * colors[4 + channel]) / 3) : (byte)0;
                }
                colors[11] = 255; colors[15] = fourColors || alphaDepth == 0 ? (byte)255 : (byte)0;
                var selectors = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(colorAt + 4, 4));
                ulong alphaSelectors = 0;
                if (encoding == Blp2Encoding.Bc3)
                {
                    alphas[0] = payload[at]; alphas[1] = payload[at + 1];
                    if (alphas[0] > alphas[1])
                        for (var index = 2; index < 8; index++) alphas[index] = (byte)(((8 - index) * alphas[0] + (index - 1) * alphas[1]) / 7);
                    else
                    {
                        for (var index = 2; index < 6; index++) alphas[index] = (byte)(((6 - index) * alphas[0] + (index - 1) * alphas[1]) / 5);
                        alphas[6] = 0; alphas[7] = 255;
                    }
                    for (var index = 0; index < 6; index++) alphaSelectors |= (ulong)payload[at + 2 + index] << (8 * index);
                }
                for (var py = 0; py < 4 && by * 4 + py < height; py++)
                    for (var px = 0; px < 4 && bx * 4 + px < width; px++)
                    {
                        var pixel = py * 4 + px; var choice = (int)((selectors >> (pixel * 2)) & 3); var target = ((by * 4 + py) * width + bx * 4 + px) * 4;
                        colors.Slice(choice * 4, 4).CopyTo(rgba.AsSpan(target, 4));
                        if (encoding == Blp2Encoding.Bc2) rgba[target + 3] = (byte)(((payload[at + pixel / 2] >> ((pixel % 2) * 4)) & 15) * 17);
                        else if (encoding == Blp2Encoding.Bc3) rgba[target + 3] = alphas[(int)((alphaSelectors >> (pixel * 3)) & 7)];
                    }
            }
        }
    }
    private static void Color565(ushort value, Span<byte> rgba)
    {
        var red = (value >> 11) & 31; var green = (value >> 5) & 63; var blue = value & 31;
        rgba[0] = (byte)((red << 3) | (red >> 2)); rgba[1] = (byte)((green << 2) | (green >> 4)); rgba[2] = (byte)((blue << 3) | (blue >> 2)); rgba[3] = 255;
    }
}
