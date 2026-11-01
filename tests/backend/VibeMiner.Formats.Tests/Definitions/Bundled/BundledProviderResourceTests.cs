using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VibeMiner.Formats.Definitions.Tests;

/// <summary>The embedded WoWDBDefs resources and the manifest rules, ported from the legacy TablesCatalogTests.</summary>
public sealed class BundledProviderResourceTests
{
    [Theory]
    [InlineData("ItemSubClass", "e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c", 28231)]
    [InlineData("ItemXItemEffect", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb", 11271)]
    [InlineData("Creature", "3644a603aae0eed595de31933c49e7bd8abea32bc1a1c32f9d754afbfb8f5682", 28281)]
    [InlineData("CreatureDisplayInfo", "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425", 38876)]
    [InlineData("CreatureModelData", "ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a", 40383)]
    [InlineData("ItemEffect", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c", 27918)]
    [InlineData("SpellName", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667", 23534)]
    public async Task Bundled_definition_resources_match_original_pinned_bytes_and_attribution(string name, string hash, int length)
    {
        var assembly = typeof(BundledDefinitions).Assembly;
        async Task<byte[]> Resource(string relative)
        {
            var suffix = BundledDefinitions.ResourceSuffix(relative);
            var resource = Assert.Single(assembly.GetManifestResourceNames(), value => value.EndsWith(suffix, StringComparison.Ordinal));
            await using var input = assembly.GetManifestResourceStream(resource)!;
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); return bytes.ToArray();
        }
        // #144: the files sit unmodified in one Brotli stream; only these resources are embedded, never a loose .dbd.
        Assert.Equal(["LICENSE.md", "NOTICE.md", "definitions.br", "manifest.json", "sources.json"], assembly.GetManifestResourceNames()
            .Where(value => value.Contains(".WoWDBDefs.", StringComparison.Ordinal)).Select(value => value[(value.IndexOf(BundledDefinitions.Revision, StringComparison.Ordinal) + 41)..])
            .Order(StringComparer.Ordinal));
        var data = PinnedDefinitionFiles.Bytes(name);
        Assert.Equal(length, data.Length); Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(data)));
        var sourcesBytes = await Resource("sources.json");
        Assert.Equal(BundledDefinitions.SourcesSha256, Convert.ToHexStringLower(SHA256.HashData(sourcesBytes)));
        using var sources = JsonDocument.Parse(sourcesBytes);
        Assert.Equal(BundledDefinitions.Revision, sources.RootElement.GetProperty("revision").GetString());
        Assert.Equal("CC-BY-SA-4.0", sources.RootElement.GetProperty("definitionLicense").GetString());
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledDefinitions.Revision}/", sources.RootElement.GetProperty("sourceBase").GetString());
        var receipt = Assert.Single(sources.RootElement.GetProperty("definitions").GetProperty("files").EnumerateArray(),
            source => source.GetProperty("path").GetString() == "definitions/" + name + ".dbd");
        Assert.Equal(hash, receipt.GetProperty("sha256").GetString()); Assert.Equal(length, receipt.GetProperty("byteLength").GetInt32());
        Assert.Equal(await Resource("definitions.br"), await File.ReadAllBytesAsync(Path.Combine(PinnedDefinitionFiles.Directory, "definitions.br")));
        // The engine reads the same bytes, names the immutable upstream URL and keeps the CC BY-SA attribution.
        var provider = await BundledDefinitions.OpenAsync(default);
        var tableId = provider.Tables.Single(entry => entry.Name == name).TableId;
        Assert.Equal(hash, provider.Require(tableId).Sha256);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledDefinitions.Revision}/definitions/{name}.dbd", provider.Require(tableId).SourceUrl);
        Assert.Contains("CC BY-SA 4.0", Encoding.UTF8.GetString(await Resource("NOTICE.md")));
        Assert.Contains("Attribution-ShareAlike 4.0", Encoding.UTF8.GetString(await Resource("LICENSE.md")));
        var manifest = await Resource("manifest.json");
        Assert.Equal(BundledDefinitions.ManifestSha256, Convert.ToHexStringLower(SHA256.HashData(manifest)));
        Assert.Equal(1346, BundledDefinitions.ParseManifest(manifest).Count);
        Assert.Equal(BundledDefinitions.ManifestSha256, provider.Info.ManifestSha256); Assert.Equal("CC-BY-SA-4.0", provider.Info.License);
    }

    [Theory]
    [InlineData("[{\"tableHash\":\"ABCD1234\"},{\"tableHash\":\"abcd1234\"}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"tableHash\":\"00000001\"}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":4294967296}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":1.5}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":0}]")]
    public void Manifest_rejects_duplicate_hashes_and_lossy_or_invalid_file_identities(string json) =>
        Assert.Equal("DEFINITION_PROVIDER_INVALID", Assert.Throws<DefinitionException>(() => BundledDefinitions.ParseManifest(Encoding.UTF8.GetBytes(json))).Code);

    [Fact]
    public void Manifest_preserves_unsigned32_identity_without_signed_int_conversion()
    {
        var entry = Assert.Single(BundledDefinitions.ParseManifest("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":4294967295}]"u8.ToArray()));
        Assert.Equal("abcd1234", entry.TableId); Assert.Equal("4294967295", entry.Db2FileDataId); Assert.Null(entry.Name);
    }
}
