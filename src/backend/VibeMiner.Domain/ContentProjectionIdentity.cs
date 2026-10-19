using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VibeMiner.Domain;

/// <summary>Canonical identity of authenticated clear spans and declared unavailable intervals; never a full decoded hash.</summary>
public static class ContentProjectionIdentity
{
    public const string Policy = "blte-clear-chunks-v1";
    public const string VerifierRevision = "content-blte-projection-v1";
    public const int MaxRanges = 128;
    public const int MaxChunks = 65_536;

    public static string Compute(string contentKey, string encodingKey, string decodedBytes, ContentProjection projection)
    {
        if (!Hex(contentKey, 32, false) || !Hex(encodingKey, 32, false) || projection is null || projection.Policy != Policy ||
            projection.ReadableRanges is null || projection.UnavailableRanges is null ||
            projection.UnavailableRanges.Count is < 1 or > MaxRanges ||
            projection.ReadableRanges.Count + projection.UnavailableRanges.Count > MaxRanges)
            throw Invalid();
        var length = Number(decodedBytes);
        if (length < 1 || length > 256L << 20) throw Invalid();
        var entries = new List<(long Offset, long Length, int? Ordinal, string Canonical)>();
        long previous = -1;
        foreach (var range in projection.ReadableRanges)
        {
            if (range is null || !Hex(range.Sha256, 64, false)) throw Invalid();
            var offset = Number(range.Offset); var count = Number(range.ByteLength);
            if (offset <= previous || count < 1 || offset > length - count) throw Invalid();
            previous = offset; entries.Add((offset, count, null, $"R {range.Offset} {range.ByteLength} {range.Sha256}\n"));
        }
        previous = -1; var ordinals = new HashSet<int>(); var previousOrdinal = -1;
        foreach (var range in projection.UnavailableRanges)
        {
            if (range is null || !Hex(range.KeyName, 16, true) || range.ChunkOrdinal is < 0 or >= MaxChunks || range.ChunkOrdinal <= previousOrdinal || !ordinals.Add(range.ChunkOrdinal)) throw Invalid();
            var offset = Number(range.Offset); var count = Number(range.ByteLength);
            if (offset <= previous || count < 1 || offset > length - count) throw Invalid();
            previous = offset; previousOrdinal = range.ChunkOrdinal;
            entries.Add((offset, count, range.ChunkOrdinal, $"E {range.Offset} {range.ByteLength} {range.ChunkOrdinal.ToString(CultureInfo.InvariantCulture)} {range.KeyName}\n"));
        }
        var text = new StringBuilder($"{Policy}\n{contentKey}\n{encodingKey}\n{decodedBytes}\n");
        long end = 0; var ordinal = 0;
        foreach (var entry in entries.OrderBy(value => value.Offset))
        {
            if (entry.Offset != end || entry.Ordinal is { } original && original < ordinal) throw Invalid();
            text.Append(entry.Canonical); end = checked(end + entry.Length); ordinal++;
        }
        if (end != length) throw Invalid();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static long Number(string? value)
    {
        if (value is null || value.Length is < 1 or > 19 || value.Length > 1 && value[0] == '0' ||
            value.Any(ch => ch is < '0' or > '9') || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) throw Invalid();
        return parsed;
    }
    private static bool Hex(string? value, int width, bool upper) => value is not null && value.Length == width &&
        value.All(ch => ch is >= '0' and <= '9' || (upper ? ch is >= 'A' and <= 'F' : ch is >= 'a' and <= 'f'));
    private static DiscoveryException Invalid() => new("CONTENT_PROJECTION_INVALID", "Readable spans and unavailable intervals do not form one bounded exact source projection.");
}
