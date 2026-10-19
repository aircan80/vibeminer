using System.Globalization;
using System.Text.RegularExpressions;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

public sealed record MetadataExpectation(string Role, string ContentKey, string EncodingKey,
    long EncodedBytes, long DecodedBytes);
public sealed record MetadataDeclaration(string Role, string ContentKey, string? EncodingKey,
    long? EncodedBytes, long? DecodedBytes)
{
    public MetadataExpectation? DirectExpectation => EncodingKey is not null && EncodedBytes is { } encoded && DecodedBytes is { } decoded
        ? new(Role, ContentKey, EncodingKey, encoded, decoded) : null;
}
public sealed record MetadataDeclarationSet(string RootContentKey, MetadataExpectation Encoding,
    MetadataDeclaration Install, MetadataDeclaration? Download, MetadataDeclaration? Size);

/// <summary>Reviewed ROOT-mode declaration policy; preserves the caller's original configuration.</summary>
public static class MetadataDeclarations
{
    public const string Revision = "wow-root-metadata-declarations-v1";
    private static readonly HashSet<string> RequiredFields = new(StringComparer.Ordinal)
    { "root", "encoding", "encoding-size", "install", "install-size", "download", "download-size", "size", "size-size" };
    private static readonly HashSet<string> InformationalFields = new(StringComparer.Ordinal)
    { "build-name", "build-uid", "build-product", "build-branch", "build-number", "build-attributes",
        "client-version", "feature-placeholder", "build-partial-priority", "product", "uid" };
    private static readonly HashSet<string> PatchFields = new(StringComparer.Ordinal)
    { "patch", "patch-size", "patch-index", "patch-index-size" };

    public static MetadataDeclarationSet Parse(TactConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var fields = configuration.Fields;
        if (!fields.TryGetValue("root", out var root) || root.Count != 1 || !IsKey(root[0]) || fields.ContainsKey("root-size"))
            throw Error("METADATA_UNSUPPORTED", "The ROOT profile requires one nonzero content key and no root-size companion; VFS fallback is unsupported.");
        var encoding = Read("encoding", true, true)!;
        var install = Read("install", true, false)!;
        var download = Read("download", false, false);
        var size = Read("size", false, false);
        foreach (var (field, values) in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequiredFields.Contains(field) || InformationalFields.Contains(field) || PatchFields.Contains(field) ||
                Regex.IsMatch(field, "^vfs-(?:root|[1-9][0-9]*)(?:-size)?$", RegexOptions.CultureInvariant)) continue;
            var segments = field.Split('-');
            if (segments.Any(segment => segment is "manifest" or "root" or "index") ||
                fields.ContainsKey(field + "-size") ||
                field.EndsWith("-size", StringComparison.Ordinal) && fields.ContainsKey(field[..^5]) ||
                values.Any(IsHashShape))
                throw Error("METADATA_UNSUPPORTED", $"Unclassified reference-like declaration under {Revision}: {field}.");
        }
        return new(root[0].ToLowerInvariant(), encoding.DirectExpectation!, install, download, size);

        MetadataDeclaration? Read(string role, bool required, bool directRequired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fields.TryGetValue(role, out var keys))
            {
                if (fields.ContainsKey(role + "-size")) throw Error("METADATA_INVALID", $"Orphan metadata length declaration: {role}-size.");
                if (required) throw Error("METADATA_MISSING", $"Required metadata declaration is missing: {role}.");
                return null;
            }
            if (keys.Count is < 1 or > 2 || keys.Any(key => !IsKey(key)) || directRequired && keys.Count != 2)
                throw Error("METADATA_INVALID", $"Malformed metadata key declaration: {role}.");
            long? decoded = null, encoded = null;
            if (fields.TryGetValue(role + "-size", out var lengths))
            {
                if (lengths.Count != 2) throw Error("METADATA_INVALID", $"Both metadata lengths are required: {role}.");
                decoded = Length(lengths[0], role);
                encoded = Length(lengths[1], role);
                if (encoded > BlteDecoder.MaxEncodedBytes || decoded > BlteDecoder.MaxDecodedBytes)
                    throw Error("METADATA_LIMIT", $"Metadata declaration exceeds the admitted byte bounds: {role}.");
            }
            else if (keys.Count == 2)
                throw Error("METADATA_INVALID", $"A direct metadata source requires encoded and decoded lengths: {role}.");
            return new(role, keys[0].ToLowerInvariant(), keys.Count == 2 ? keys[1].ToLowerInvariant() : null, encoded, decoded);
        }
    }

    private static long Length(string value, string role)
    {
        if (value.Length == 0 || value[0] is < '1' or > '9' || value.Any(ch => ch is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw Error("METADATA_INVALID", $"A positive canonical metadata length is required: {role}.");
        return length;
    }
    private static bool IsHashShape(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);
    private static bool IsKey(string value) => IsHashShape(value) && value.Any(ch => ch != '0');
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
