using System.Globalization;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Formats;

/// <summary>A metadata object named by both keys: the content key (decoded MD5) and the encoding key (BLTE object).</summary>
internal readonly record struct KeyPair(CdnKey CKey, CdnKey EKey);

/// <summary>A metadata object named by its content key and, in most builds, its encoding key too.</summary>
internal readonly record struct ConfigKeys(CdnKey CKey, CdnKey? EKey);

/// <summary>A <c>*-size</c> pair: the decoded length, then the encoded (BLTE) length.</summary>
internal readonly record struct SizePair(long Decoded, long Encoded);

/// <summary>
/// A build config, read under the same rules as the engine's <c>MetadataDeclarations</c>. <c>root</c> (one key),
/// <c>encoding</c> (both keys) and <c>install</c> are required; <c>download</c> and <c>size</c> are optional. An object
/// named by both keys needs its <c>*-size</c>; a <c>*-size</c> needs its object and exactly two positive lengths within the
/// BLTE limits (else Corrupt <c>BUILD_CONFIG_LIMIT</c>). <c>root-size</c> (a VFS root) is Unsupported
/// <c>BUILD_CONFIG_UNSUPPORTED</c>. Anything else malformed is Corrupt <c>BUILD_CONFIG_INVALID</c>; grammar failures keep
/// the <see cref="TactConfig"/> codes.
/// </summary>
internal sealed record BuildConfigFile(CdnKey Root, KeyPair Encoding, SizePair EncodingSize,
    ConfigKeys Install, SizePair? InstallSize, ConfigKeys? Download, SizePair? DownloadSize,
    ConfigKeys? Size, SizePair? SizeSize, string? BuildName, IReadOnlyDictionary<string, IReadOnlyList<string>> Fields)
{
    private const string Code = "BUILD_CONFIG_INVALID";

    public static BuildConfigFile Parse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        var fields = TactConfig.Parse(bytes, cancellationToken).Fields;
        if (!fields.TryGetValue("root", out var root)) throw ConfigValues.Invalid(Code, "The build config has no root.");
        if (root.Count != 1) throw ConfigValues.Invalid(Code, "The build config root needs one key.");
        var rootKey = ConfigValues.Key(Code, root[0], "root");
        if (fields.ContainsKey("root-size"))
            throw new BuildFormatException(BuildFailure.Unsupported, "BUILD_CONFIG_UNSUPPORTED", "The build config has root-size (a VFS root).");
        var (encoding, encodingSize) = Entry(fields, "encoding");
        if (encoding is not { EKey: { } encodingEKey }) throw ConfigValues.Invalid(Code, "The build config encoding needs both keys.");
        var (install, installSize) = Entry(fields, "install");
        if (install is null) throw ConfigValues.Invalid(Code, "The build config has no install.");
        var (download, downloadSize) = Entry(fields, "download");
        var (size, sizeSize) = Entry(fields, "size");
        var buildName = fields.TryGetValue("build-name", out var name) && name.Count > 0 ? string.Join(' ', name) : null;
        return new(rootKey, new(encoding.Value.CKey, encodingEKey), encodingSize!.Value, install.Value, installSize,
            download, downloadSize, size, sizeSize, buildName, fields);
    }

    private static (ConfigKeys? Keys, SizePair? Sizes) Entry(IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string role)
    {
        var hasSizes = fields.TryGetValue(role + "-size", out var sizes);
        if (!fields.TryGetValue(role, out var keys))
        {
            if (hasSizes) throw ConfigValues.Invalid(Code, $"The build config has {role}-size without {role}.");
            return (null, null);
        }
        if (keys.Count is < 1 or > 2) throw ConfigValues.Invalid(Code, $"The build config {role} needs one or two keys.");
        var entry = new ConfigKeys(ConfigValues.Key(Code, keys[0], role), keys.Count == 2 ? ConfigValues.Key(Code, keys[1], role) : null);
        if (!hasSizes)
        {
            if (keys.Count == 2) throw ConfigValues.Invalid(Code, $"The build config {role} has both keys but no {role}-size.");
            return (entry, null);
        }
        if (sizes!.Count != 2) throw ConfigValues.Invalid(Code, $"The build config {role}-size needs two lengths.");
        var pair = new SizePair(ConfigValues.Length(Code, sizes[0], role + "-size"), ConfigValues.Length(Code, sizes[1], role + "-size"));
        if (pair.Decoded > Blte.MaxDecodedBytes || pair.Encoded > Blte.MaxEncodedBytes)
            throw new BuildFormatException(BuildFailure.Corrupt, "BUILD_CONFIG_LIMIT", $"The build config {role}-size is over the BLTE limits.");
        return (entry, pair);
    }
}

/// <summary>
/// A CDN config. <c>archives</c> is required and kept in file order (an archive's ordinal is its position); it may be empty.
/// <c>archives-index-size</c> is a hint, not authentication, and must match the archive count when present. Anything
/// malformed is Corrupt <c>CDN_CONFIG_INVALID</c>; grammar failures keep the <see cref="TactConfig"/> codes.
/// </summary>
internal sealed record CdnConfigFile(IReadOnlyList<CdnKey> Archives, IReadOnlyList<long>? ArchiveIndexSizes,
    CdnKey? FileIndex, long? FileIndexSize, CdnKey? ArchiveGroup, IReadOnlyDictionary<string, IReadOnlyList<string>> Fields)
{
    private const string Code = "CDN_CONFIG_INVALID";

    public static CdnConfigFile Parse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        var fields = TactConfig.Parse(bytes, cancellationToken).Fields;
        if (!fields.TryGetValue("archives", out var listed)) throw ConfigValues.Invalid(Code, "The CDN config has no archives.");
        var archives = new CdnKey[listed.Count];
        for (var i = 0; i < archives.Length; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            archives[i] = ConfigValues.Key(Code, listed[i], "archives");
        }
        long[]? indexSizes = null;
        if (fields.TryGetValue("archives-index-size", out var sizes))
        {
            if (sizes.Count != archives.Length) throw ConfigValues.Invalid(Code, "The CDN config archives-index-size doesn't match the archives.");
            indexSizes = new long[sizes.Count];
            for (var i = 0; i < indexSizes.Length; i++) indexSizes[i] = ConfigValues.Length(Code, sizes[i], "archives-index-size");
        }
        var fileIndex = Single(fields, "file-index", value => ConfigValues.Key(Code, value, "file-index"));
        var fileIndexSize = Single(fields, "file-index-size", value => ConfigValues.Length(Code, value, "file-index-size"));
        if (fileIndexSize is not null && fileIndex is null) throw ConfigValues.Invalid(Code, "The CDN config has file-index-size without file-index.");
        var archiveGroup = Single(fields, "archive-group", value => ConfigValues.Key(Code, value, "archive-group"));
        return new(Array.AsReadOnly(archives), indexSizes is null ? null : Array.AsReadOnly(indexSizes),
            fileIndex, fileIndexSize, archiveGroup, fields);
    }

    private static T? Single<T>(IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string name, Func<string, T> parse) where T : struct
    {
        if (!fields.TryGetValue(name, out var values)) return null;
        if (values.Count != 1) throw ConfigValues.Invalid(Code, $"The CDN config {name} needs one value.");
        return parse(values[0]);
    }
}

internal static class ConfigValues
{
    /// <summary>A key is 32 hex digits and not all zero.</summary>
    public static CdnKey Key(string code, string value, string field)
    {
        if (value.Length != 32 || !value.All(char.IsAsciiHexDigit) || value.All(ch => ch == '0'))
            throw Invalid(code, $"The {field} key is invalid.");
        return CdnKey.Parse(value);
    }

    /// <summary>A length is a positive canonical decimal (no sign, no leading zero).</summary>
    public static long Length(string code, string value, string field)
    {
        if (value.Length == 0 || value[0] is < '1' or > '9' || value.Any(ch => ch is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw Invalid(code, $"A {field} length is invalid.");
        return length;
    }

    public static BuildFormatException Invalid(string code, string message) => new(BuildFailure.Corrupt, code, message);
}
