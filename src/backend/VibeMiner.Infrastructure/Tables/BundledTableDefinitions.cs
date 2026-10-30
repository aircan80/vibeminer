using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record BundledTableDefinition(string Sha256, string SourceUrl, DbdDocument Document);

internal sealed class BundledTableDefinitions
{
    internal const string Revision = "b9a63096cdd99b26967f17f9515377ae62cadfd7";
    internal const string ManifestSha256 = "f7b752a991d6a0321d07ecb7f06e62cd94694c1594d2fe5ce0cb7b243c71559a";
    private const string Origin = "https://raw.githubusercontent.com/wowdev/WoWDBDefs/" + Revision + "/";
    private sealed record DefinitionSource(string TableId, string Name, string Sha256);
    private static readonly DefinitionSource[] Sources =
    [
        new("c9d6b6b3", "Creature", "3644a603aae0eed595de31933c49e7bd8abea32bc1a1c32f9d754afbfb8f5682"),
        new("bfdaf9f1", "CreatureDisplayInfo", "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425"),
        new("7f695fa5", "CreatureModelData", "ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a"),
        new("1f80ad3f", "CreatureType", "9ce11380b21c914d52901468386fbe66abaf3ccdad3fabdf4553c9b681194e49"),
        new("369f469f", "CreatureFamily", "da9a578d08b3d2c97283ca272e8b5cbe4fc7036517e584053aaab3bb3495db90"),
        new("50238ec2", "Item", "076ea4c7e80c891b399cc77f285c807071080c0fed95701e447f7c98024ae120"),
        new("919be54e", "ItemSparse", "8664c3b5862dfe4c3a4322941b48b032c57b90412c070deb2028f3e448cf674a"),
        new("b977271e", "ItemClass", "6e0517e1ae17d3528aefe3eac6ad2b417ae7361d8d5ad77d0c743ffd4d126151"),
        new("4002a5b1", "ItemEffect", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c"),
        new("95d8638f", "ItemSubClass", "e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c"),
        new("00cb674f", "ItemXItemEffect", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb"),
        new("a4c11069", "Material", "94332b8ef22c9efa431f6ec35690a92896daf05c7f677fa6ce049006ab487ae7"),
        new("46c66698", "SpellName", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667"),
        new("9f4206c9", "ItemGroupSounds", "e108620add5ef881b6189ee7f8c0bb0962c59254ec0dfe405abc889c9061f6de")
    ];
    /// <summary>The bundled table name for an exact table ID, or null when it is not bundled.</summary>
    internal static string? NameOf(string tableId) => Array.Find(Sources, source => source.TableId == tableId)?.Name;
    internal TableDefinitionProvider Info { get; } = new("WoWDBDefs contributors", Revision, ManifestSha256,
        "CC-BY-SA-4.0", Origin + "manifest.json");
    internal required IReadOnlyList<TableCatalogEntry> Entries { get; init; }
    internal required IReadOnlyDictionary<string, BundledTableDefinition> Definitions { get; init; }

    internal static async Task<BundledTableDefinitions> LoadAsync()
    {
        var manifest = await ResourceAsync("manifest.json", ManifestSha256);
        var entries = ParseManifest(manifest);
        if (entries.Count != 1346) throw Error("The pinned provider catalogue does not contain its expected complete entry count.");
        var definitions = new Dictionary<string, BundledTableDefinition>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            var entry = entries.SingleOrDefault(entry => entry.TableId == source.TableId);
            if (entry?.Name != source.Name) throw Error("A bundled definition differs from its pinned manifest table identity.");
            var bytes = await ResourceAsync("definitions/" + source.Name + ".dbd", source.Sha256);
            definitions.Add(source.TableId, new(source.Sha256, Origin + "definitions/" + source.Name + ".dbd", DbdDefinitionReader.Parse(bytes)));
        }
        return new()
        {
            Entries = Array.AsReadOnly(entries.Select(entry => entry with { DefinitionState = definitions.ContainsKey(entry.TableId) ? "bundled" : "notBundled" })
                .OrderBy(entry => entry.DefinitionState == "bundled" ? 0 : 1)
                .ThenBy(entry => entry.Name ?? entry.TableId, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.TableId, StringComparer.Ordinal).ToArray()),
            Definitions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, BundledTableDefinition>(definitions)
        };
    }

    internal static IReadOnlyList<TableCatalogEntry> ParseManifest(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 1 << 20) throw Error("The provider manifest exceeds its byte bound.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 4096)
                throw Error("A provider manifest requires a bounded array of table entries.");
            var entries = new List<TableCatalogEntry>(); var ids = new HashSet<string>(StringComparer.Ordinal);
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
                    name = text;
                }
                var db2 = FileDataId("db2FileDataID"); _ = FileDataId("dbcFileDataID");
                entries.Add(new(tableId.ToLowerInvariant(), name, db2, "notBundled"));
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

    private static async Task<byte[]> ResourceAsync(string relative, string expectedHash)
    {
        var assembly = typeof(BundledTableDefinitions).Assembly;
        var suffix = ".Tables.Resources.WoWDBDefs." + Revision + "." + relative.Replace('/', '.');
        var names = assembly.GetManifestResourceNames().Where(name => name.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        if (names.Length != 1) throw Error("A pinned provider resource is missing or ambiguous.");
        await using var stream = assembly.GetManifestResourceStream(names[0]) ?? throw Error("A pinned provider resource could not be opened.");
        if (stream.Length is < 1 or > (1 << 20)) throw Error("A pinned provider resource exceeds its byte bound.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash) throw Error("A bundled provider resource failed its pinned SHA-256 verification.");
        return bytes;
    }
    private static DiscoveryException Error(string message) => new("DEFINITION_PROVIDER_INVALID", message);
}
