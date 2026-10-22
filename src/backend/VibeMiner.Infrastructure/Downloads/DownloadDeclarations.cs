using System.Globalization;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>DL-scoped declaration policy. Unrelated research roles are preserved in the original config.</summary>
internal static class DownloadDeclarations
{
    internal static MetadataDeclaration? ReadDownload(TactConfiguration configuration) => Read(configuration, "download", false);
    /// <summary>The install manifest declaration, if the build has one (D071: Files lists its names).</summary>
    internal static MetadataDeclaration? ReadInstall(TactConfiguration configuration) => Read(configuration, "install", false);
    internal static MetadataExpectation ReadEncoding(TactConfiguration configuration) =>
        Read(configuration, "encoding", true)?.DirectExpectation ??
        throw new DiscoveryException("METADATA_UNSUPPORTED", "Resolving this download manifest requires a direct, sized encoding metadata source.");

    private static MetadataDeclaration? Read(TactConfiguration configuration, string role, bool required)
    {
        if (!configuration.Fields.TryGetValue(role, out var keys))
        {
            if (configuration.Fields.ContainsKey(role + "-size")) throw Invalid(role);
            if (required) throw new DiscoveryException("METADATA_MISSING", "Encoding metadata is required to resolve this download manifest.");
            return null;
        }
        if (keys.Count is < 1 or > 2 || keys.Any(key => key.Length != 32 || !key.All(char.IsAsciiHexDigit) || key.All(c => c == '0')))
            throw Invalid(role);
        long? encoded = null, decoded = null;
        if (configuration.Fields.TryGetValue(role + "-size", out var lengths))
        {
            if (lengths.Count != 2) throw Invalid(role);
            decoded = Number(lengths[0]); encoded = Number(lengths[1]);
            if (encoded > BlteDecoder.MaxEncodedBytes || decoded > BlteDecoder.MaxDecodedBytes)
                throw new DiscoveryException("METADATA_LIMIT", "Download preparation metadata exceeds the supported byte limits.");
        }
        return new(role, keys[0].ToLowerInvariant(), keys.Count == 2 ? keys[1].ToLowerInvariant() : null, encoded, decoded);
    }
    private static long Number(string value)
    {
        if (value.Length is < 1 or > 19 || value[0] is < '1' or > '9' || !value.All(char.IsAsciiDigit) ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new DiscoveryException("METADATA_INVALID", "A metadata length must be a positive canonical integer.");
        return number;
    }
    private static DiscoveryException Invalid(string role) => new("METADATA_INVALID", $"The {role} metadata declaration is malformed.");
}
