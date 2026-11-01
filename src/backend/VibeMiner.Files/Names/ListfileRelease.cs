using System.Globalization;
using System.Text.Json;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Files.Names;

/// <summary>A refused listfile release, part or attempt, with its <c>LISTFILE_*</c> code and layer 2's observation time.</summary>
internal sealed class ListfileFetchException(string code, string message, DateTimeOffset? observedAt = null) : Exception(message)
{
    public string Code { get; } = code;
    public DateTimeOffset? ObservedAt { get; } = observedAt;
}

/// <summary>
/// The latest wow-listfile release as GitHub's releases/latest JSON describes it (D105), with the checks of the legacy
/// <c>CommunityListfile.LatestAsync</c>: a tag (here also layer 2's part product rule, 1-64 of [a-z0-9_-]), published_at,
/// and the <see cref="WowListfileObservations.AssetName"/> asset with GitHub's <c>sha256:</c> digest, a size of 1 byte to
/// 512 MiB and its download address exactly <see cref="WowListfileObservations.AssetUrl"/>.
/// </summary>
internal sealed record ListfileRelease(string Tag, DateTimeOffset PublishedAt, string Sha256, long Size)
{
    public const long MaxAssetBytes = (long)ObservationRequest.ListfileMaxParts * ObservationRequest.ListfilePartBytes;
    public const int MaxReleaseBytes = 1 << 20;

    public int Parts => (int)((Size + ObservationRequest.ListfilePartBytes - 1) / ObservationRequest.ListfilePartBytes);

    /// <summary>The exact length of part <paramref name="index"/>: 8 MiB, the last one the remainder.</summary>
    public long PartLength(int index) => index < Parts - 1 ? ObservationRequest.ListfilePartBytes : Size - (long)(Parts - 1) * ObservationRequest.ListfilePartBytes;

    /// <summary>Throws <see cref="ListfileFetchException"/> (LISTFILE_INVALID, _TOO_LARGE or _NOT_FOUND) for anything else.</summary>
    public static ListfileRelease Parse(ReadOnlySpan<byte> bytes, DateTimeOffset? observedAt = null)
    {
        if (bytes.Length > MaxReleaseBytes) throw FileNames.Invalid("GitHub sent a larger release answer than VibeMiner allows.", observedAt);
        var unknown = FileNames.Invalid("GitHub sent a release answer VibeMiner doesn’t understand.", observedAt);
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String
                || tagValue.GetString() is not { } tag
                || !root.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(published.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var publishedAt)
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw unknown;
            if (!ValidTag(tag)) throw FileNames.Invalid("The release’s tag isn’t one VibeMiner allows.", observedAt);
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || name.GetString() != WowListfileObservations.AssetName) continue;
                if (!asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size < 1)
                    throw unknown;
                if (size > MaxAssetBytes) throw FileNames.TooLarge(observedAt);
                // Without GitHub's own digest the bytes can't be checked, so they aren't fetched.
                if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String || digest.GetString() is not { Length: 71 } text
                    || !text.StartsWith("sha256:", StringComparison.Ordinal) || !Hex(text[7..]))
                    throw FileNames.Invalid("GitHub didn’t publish a SHA-256 for the file, so it can’t be checked.", observedAt);
                if (!asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String
                    || url.GetString() != WowListfileObservations.AssetUrl(tag))
                    throw FileNames.Invalid("The release’s download address isn’t one VibeMiner allows.", observedAt);
                return new(tag, publishedAt, text[7..], size);
            }
            throw FileNames.NotFound(observedAt);
        }
        catch (JsonException) { throw unknown; }
    }

    /// <summary>Layer 2's part product rule: 1-64 of [a-z0-9_-].</summary>
    internal static bool ValidTag(string tag) => tag.Length is >= 1 and <= 64 && tag.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool Hex(string text) => text.Length == 64 && text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
