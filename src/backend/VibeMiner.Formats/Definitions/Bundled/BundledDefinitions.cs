using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace VibeMiner.Formats.Definitions;

/// <summary>A table the pinned manifest lists: its id (8 lower hex, the manifest tableHash), its name when the manifest
/// has one, its DB2 FileDataID as exact unsigned32 text, and whether a definition file for it is bundled.</summary>
public sealed record DefinitionTable(string TableId, string? Name, string? Db2FileDataId, bool Bundled);

/// <summary>Who the bundled definitions come from, for attribution: the provider, its pinned revision, the manifest's
/// SHA-256, the definitions' license and the immutable manifest URL.</summary>
public sealed record DefinitionProviderInfo(string Name, string Revision, string ManifestSha256, string License, string SourceUrl);

/// <summary>A bundled table's definition: the pinned file's SHA-256, its immutable upstream URL and the parsed document.</summary>
public sealed record BundledDefinition(string Sha256, string SourceUrl, DbdDocument Document);

/// <summary>
/// The pinned WoWDBDefs provider (#144, D120): the manifest of every table and all 1,346 definition files, kept unmodified
/// in one Brotli stream. sources.json lists each file's path, SHA-256 and length in stream order; its own SHA-256 is
/// pinned here, and the stream's is checked against it at load. Definitions are read and parsed only when a table
/// asks for one, through a bounded cache (<see cref="BundledDefinitionCache"/>).
/// </summary>
public sealed class BundledDefinitions
{
    internal const string Revision = "b9a63096cdd99b26967f17f9515377ae62cadfd7";
    internal const string ManifestSha256 = "f7b752a991d6a0321d07ecb7f06e62cd94694c1594d2fe5ce0cb7b243c71559a";
    internal const string SourcesSha256 = "cc3119202a5f74b5af9318212d2468d3e51659cf7f9f85446cb72bb9d15555dd";
    internal const string Origin = "https://raw.githubusercontent.com/wowdev/WoWDBDefs/" + Revision + "/";
    internal const int MaxDefinitionBytes = 1 << 20;
    private const int MaxResourceBytes = 1 << 20, MaxStreamBytes = 4 << 20, MaxFiles = 4096;
    private const long MaxDecodedBytes = 64L << 20;

    // The tables features read (D101), with the exact bytes they were reviewed against. Load checks them, so a feature
    // never reads a definition other than the one its profile names.
    private static readonly (string TableId, string Name, string Sha256)[] FeatureTables =
    [
        ("c9d6b6b3", "Creature", "3644a603aae0eed595de31933c49e7bd8abea32bc1a1c32f9d754afbfb8f5682"),
        ("bfdaf9f1", "CreatureDisplayInfo", "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425"),
        ("7f695fa5", "CreatureModelData", "ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a"),
        ("1f80ad3f", "CreatureType", "9ce11380b21c914d52901468386fbe66abaf3ccdad3fabdf4553c9b681194e49"),
        ("369f469f", "CreatureFamily", "da9a578d08b3d2c97283ca272e8b5cbe4fc7036517e584053aaab3bb3495db90"),
        ("50238ec2", "Item", "076ea4c7e80c891b399cc77f285c807071080c0fed95701e447f7c98024ae120"),
        ("919be54e", "ItemSparse", "8664c3b5862dfe4c3a4322941b48b032c57b90412c070deb2028f3e448cf674a"),
        ("b977271e", "ItemClass", "6e0517e1ae17d3528aefe3eac6ad2b417ae7361d8d5ad77d0c743ffd4d126151"),
        ("4002a5b1", "ItemEffect", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c"),
        ("95d8638f", "ItemSubClass", "e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c"),
        ("00cb674f", "ItemXItemEffect", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb"),
        ("a4c11069", "Material", "94332b8ef22c9efa431f6ec35690a92896daf05c7f677fa6ce049006ab487ae7"),
        ("46c66698", "SpellName", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667"),
        ("9f4206c9", "ItemGroupSounds", "e108620add5ef881b6189ee7f8c0bb0962c59254ec0dfe405abc889c9061f6de")
    ];

    /// <summary>The one pinned provider, loaded and verified once on a worker the first time anyone asks for it.</summary>
    internal static readonly Lazy<Task<BundledDefinitions>> Shared = new(() => Task.Run(LoadPinnedAsync));

    /// <summary>The pinned provider, shared by every caller. A provider that fails its checks throws
    /// DEFINITION_PROVIDER_INVALID for every caller; cancelling stops only this caller's wait.</summary>
    public static Task<BundledDefinitions> OpenAsync(CancellationToken token) => Shared.Value.WaitAsync(token);

    /// <summary>A feature table's name for messages, or null for any other table.</summary>
    internal static string? NameOf(string tableId) => Array.Find(FeatureTables, table => table.TableId == tableId).Name;

    public DefinitionProviderInfo Info { get; } = new("WoWDBDefs contributors", Revision, ManifestSha256,
        "CC-BY-SA-4.0", Origin + "manifest.json");

    /// <summary>Every manifest table: bundled ones first, then by name (or id) ignoring case, then by id.</summary>
    public IReadOnlyList<DefinitionTable> Tables { get; }
    internal BundledDefinitionCache Definitions { get; }

    private BundledDefinitions(IReadOnlyList<DefinitionTable> tables, BundledDefinitionCache definitions) => (Tables, Definitions) = (tables, definitions);

    /// <summary>The definition a table reads with. Throws DEFINITION_NOT_BUNDLED when the table has no file,
    /// DEFINITION_CORRUPT when the file fails its pinned check and DEFINITION_UNSUPPORTED when it doesn't parse.</summary>
    public BundledDefinition Require(string tableId) => Definitions.Require(tableId);

    /// <summary>True with the parsed definition. False with a failure when the table's file can't be used, or with no
    /// failure when the table has no bundled file.</summary>
    public bool TryGet(string tableId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BundledDefinition? definition,
        out DefinitionException? failure) => Definitions.TryGet(tableId, out definition, out failure);

    private static async Task<BundledDefinitions> LoadPinnedAsync()
    {
        var manifest = await ResourceAsync("manifest.json", ManifestSha256);
        var sources = await ResourceAsync("sources.json", SourcesSha256);
        var provider = await CreateAsync(manifest, sources, () => OpenResource("definitions.br"), new(), CancellationToken.None);
        if (provider.Tables.Count != 1346 || provider.Definitions.Count != 1346)
            throw Error("The pinned provider catalogue does not contain its expected complete entry and definition count.");
        foreach (var (tableId, name, sha256) in FeatureTables)
            if (provider.Tables.SingleOrDefault(entry => entry.TableId == tableId)?.Name != name || provider.Definitions.File(tableId)?.Sha256 != sha256)
                throw Error("A feature table's definition differs from its pinned identity.");
        return provider;
    }

    /// <summary>A provider over a manifest, its sources list and the definition stream they describe. Every named
    /// manifest entry with a listed file is bundled; a listed file no entry names, a file outside its bounds, or a
    /// stream whose length or SHA-256 differs from the list fails the whole provider. Nothing is decoded here.</summary>
    internal static async Task<BundledDefinitions> CreateAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> sources,
        Func<Stream> stream, BundledDefinitionCacheLimits limits, CancellationToken token)
    {
        var entries = ParseManifest(manifest);
        var (expected, listed) = ParseSources(sources);
        var names = entries.Where(entry => entry.Name is not null).ToDictionary(entry => entry.Name!, StringComparer.Ordinal);
        var files = new Dictionary<string, BundledDefinitionFile>(StringComparer.Ordinal);
        foreach (var (name, sha256, length, offset) in listed)
        {
            if (!names.TryGetValue(name, out var entry)) throw Error("A bundled definition file belongs to no table in the pinned manifest.");
            files.Add(entry.TableId, new(entry.TableId, name, Origin + "definitions/" + name + ".dbd", sha256, length, offset));
        }
        await using (var input = stream())
        {
            if (!input.CanSeek || input.Length != expected.ByteLength) throw Error("The bundled definition stream differs from its listed length.");
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != expected.Sha256)
                throw Error("The bundled definition stream failed its listed SHA-256 verification.");
        }
        return new(Array.AsReadOnly(entries.Select(entry => entry with { Bundled = files.ContainsKey(entry.TableId) })
                .OrderBy(entry => entry.Bundled ? 0 : 1)
                .ThenBy(entry => entry.Name ?? entry.TableId, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.TableId, StringComparer.Ordinal).ToArray()),
            new(files, stream, limits));
    }

    internal static IReadOnlyList<DefinitionTable> ParseManifest(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxResourceBytes) throw Error("The provider manifest exceeds its byte bound.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaxFiles)
                throw Error("A provider manifest requires a bounded array of table entries.");
            var entries = new List<DefinitionTable>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in document.RootElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) throw Error("A provider table entry is not an object.");
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    if (property.Name is not ("tableHash" or "tableName" or "db2FileDataID" or "dbcFileDataID") || !properties.Add(property.Name))
                        throw Error("A provider table entry has unsupported or repeated properties.");
                if (!value.TryGetProperty("tableHash", out var hash) || hash.ValueKind != JsonValueKind.String ||
                    hash.GetString() is not { Length: 8 } tableId || !tableId.All(char.IsAsciiHexDigit) || !ids.Add(tableId.ToLowerInvariant()))
                    throw Error("A provider table hash is missing, malformed or duplicated.");
                string? name = null;
                if (value.TryGetProperty("tableName", out var nameValue))
                {
                    if (nameValue.ValueKind != JsonValueKind.String || nameValue.GetString() is not { Length: > 0 and <= 128 } text ||
                        text.Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
                        throw Error("A provider table name is malformed.");
                    // A name picks its definition file, so two entries can't share one.
                    if (!names.Add(text)) throw Error("A provider table name is duplicated.");
                    name = text;
                }
                var db2 = FileDataId("db2FileDataID"); _ = FileDataId("dbcFileDataID");
                entries.Add(new(tableId.ToLowerInvariant(), name, db2, false));
                string? FileDataId(string field)
                {
                    if (!value.TryGetProperty(field, out var id)) return null;
                    var raw = id.GetRawText();
                    if (id.ValueKind != JsonValueKind.Number || raw.Length == 0 || raw[0] is < '1' or > '9' || raw.Any(ch => ch is < '0' or > '9') || !id.TryGetUInt32(out var parsed))
                        throw Error("A provider file identity must be an exact positive unsigned32 integer.");
                    return parsed.ToString(CultureInfo.InvariantCulture);
                }
            }
            return entries.AsReadOnly();
        }
        catch (JsonException) { throw Error("The provider manifest is not valid bounded JSON."); }
    }

    internal sealed record StreamListing(string Sha256, long ByteLength, long DecodedLength);

    /// <summary>sources.json: the stream's SHA-256 and lengths, then every definition file in stream order with the offset
    /// it starts at. Paths are definitions/&lt;table name&gt;.dbd; each file holds 1 byte to 1 MiB and the lengths add up
    /// to the decoded length exactly.</summary>
    internal static (StreamListing Stream, IReadOnlyList<(string Name, string Sha256, int ByteLength, long Offset)> Files) ParseSources(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxResourceBytes) throw Error("The provider sources list exceeds its byte bound.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 5, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "revision") != Revision || Text(root, "definitionLicense") != "CC-BY-SA-4.0" ||
                Text(root, "sourceBase") != Origin || !root.TryGetProperty("definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Object ||
                Text(definitions, "path") != "definitions.br" || Text(definitions, "compression") != "brotli")
                throw Error("The provider sources list doesn't describe this pinned Brotli definition stream.");
            var stream = new StreamListing(Hash(definitions), Length(definitions, "byteLength", MaxStreamBytes), Length(definitions, "decodedLength", MaxDecodedBytes));
            if (!definitions.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaxFiles)
                throw Error("The provider sources list requires a bounded array of definition files.");
            var files = new List<(string, string, int, long)>(); var seen = new HashSet<string>(StringComparer.Ordinal); long offset = 0;
            foreach (var file in list.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object || file.EnumerateObject().Count() != 3 ||
                    Text(file, "path") is not { } path || !path.StartsWith("definitions/", StringComparison.Ordinal) || !path.EndsWith(".dbd", StringComparison.Ordinal))
                    throw Error("A listed definition file is malformed.");
                var name = path["definitions/".Length..^".dbd".Length];
                if (name.Length is < 1 or > 128 || name.Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) || !seen.Add(name))
                    throw Error("A listed definition file name is malformed or repeated.");
                var length = (int)Length(file, "byteLength", MaxDefinitionBytes);
                files.Add((name, Hash(file), length, offset)); offset += length;
            }
            if (offset != stream.DecodedLength) throw Error("The listed definition files don't add up to the stream's decoded length.");
            return (stream, files.AsReadOnly());
        }
        catch (JsonException) { throw Error("The provider sources list is not valid bounded JSON."); }
        static string? Text(JsonElement value, string name) =>
            value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        static string Hash(JsonElement value) => Text(value, "sha256") is { Length: 64 } hash && hash.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? hash : throw Error("A listed SHA-256 is malformed.");
        static long Length(JsonElement value, string name, long max) =>
            value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var length) && length >= 1 && length <= max
                ? length : throw Error("A listed byte length is missing or outside its bound.");
    }

    /// <summary>The resource suffix every pinned file is embedded under (the csproj sets each LogicalName to
    /// VibeMiner.Formats.Definitions.Bundled.WoWDBDefs.&lt;revision&gt;.&lt;file&gt;).</summary>
    internal static string ResourceSuffix(string relative) => ".WoWDBDefs." + Revision + "." + relative.Replace('/', '.');

    private static Stream OpenResource(string relative)
    {
        var assembly = typeof(BundledDefinitions).Assembly;
        var suffix = ResourceSuffix(relative);
        var names = assembly.GetManifestResourceNames().Where(name => name.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        if (names.Length != 1) throw Error("A pinned provider resource is missing or ambiguous.");
        return assembly.GetManifestResourceStream(names[0]) ?? throw Error("A pinned provider resource could not be opened.");
    }

    private static async Task<byte[]> ResourceAsync(string relative, string expectedHash)
    {
        await using var stream = OpenResource(relative);
        if (stream.Length is < 1 or > MaxResourceBytes) throw Error("A pinned provider resource exceeds its byte bound.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash) throw Error("A bundled provider resource failed its pinned SHA-256 verification.");
        return bytes;
    }
    internal static DefinitionException Error(string message) => new("DEFINITION_PROVIDER_INVALID", message);
}
