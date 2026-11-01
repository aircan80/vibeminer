using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VibeMiner.Formats.Definitions.Tests;

/// <summary>#144: all pinned WoWDBDefs definitions are bundled, verified against sources.json and parsed on demand.</summary>
public sealed class BundledDefinitionsTests
{
    private const string Good = "COLUMNS\nint ID\nint Value\n\nLAYOUT 00000001\nBUILD 1.2.3.4\n$noninline,id$ID<32>\nValue<32>\n";
    private const string Other = "COLUMNS\nint ID\nfloat Scale\n\nLAYOUT 00000002\nBUILD 1.2.3.4\n$noninline,id$ID<32>\nScale\n";
    private const string Placeholder = "COLUMNS\n";
    private const string BadBlock = "COLUMNS\nint ID\nint Wide\n\nBUILD 1.2.3.4\n$noninline,id$ID<32>\nWide<24>\n";

    /// <summary>An authored provider: a manifest, a sources list and its Brotli stream, with one hook to damage each.</summary>
    private sealed record Bundle(byte[] Manifest, byte[] Sources, byte[] Stream)
    {
        internal static Bundle Of(params (string Name, string Text)[] files) => Of(files, null);
        internal static Bundle Of((string Name, string Text)[] files, Func<string, string, string>? listedSha)
        {
            var manifest = "[" + string.Join(",", files.Select((file, index) => $"{{\"tableName\":\"{file.Name}\",\"tableHash\":\"{index + 1:X8}\",\"db2FileDataID\":{index + 100}}}")) + "]";
            var decoded = files.SelectMany(file => Encoding.UTF8.GetBytes(file.Text)).ToArray();
            var stream = new byte[BrotliEncoder.GetMaxCompressedLength(decoded.Length)];
            Assert.True(BrotliEncoder.TryCompress(decoded, stream, out var written, 11, 24)); stream = stream[..written];
            var listed = files.Select(file =>
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Text)));
                return new { path = $"definitions/{file.Name}.dbd", sha256 = listedSha?.Invoke(file.Name, sha) ?? sha, byteLength = Encoding.UTF8.GetByteCount(file.Text) };
            });
            var sources = JsonSerializer.SerializeToUtf8Bytes(new
            {
                provider = "WoWDBDefs contributors", revision = BundledDefinitions.Revision, definitionLicense = "CC-BY-SA-4.0",
                sourceBase = BundledDefinitions.Origin, resources = Array.Empty<object>(),
                definitions = new { path = "definitions.br", compression = "brotli", sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
                    byteLength = stream.Length, decodedLength = decoded.Length, files = listed }
            });
            return new(Encoding.UTF8.GetBytes(manifest), sources, stream);
        }
        internal Task<BundledDefinitions> LoadAsync(BundledDefinitionCacheLimits? limits = null) =>
            BundledDefinitions.CreateAsync(Manifest, Sources, () => new MemoryStream(Stream, writable: false), limits ?? new(), default);
    }

    private static string Id(BundledDefinitions provider, string name) => provider.Tables.Single(entry => entry.Name == name).TableId;

    [Fact]
    public async Task Loading_reads_no_definition_and_each_is_read_once_then_cached()
    {
        var provider = await Bundle.Of(("Alpha", Good), ("Beta", Other)).LoadAsync();
        Assert.Equal(0, provider.Definitions.Reads); Assert.Equal(0, provider.Definitions.CachedCount);
        Assert.All(provider.Tables, entry => Assert.True(entry.Bundled));
        var alpha = provider.Definitions[Id(provider, "Alpha")];
        Assert.Equal(1, provider.Definitions.Reads);
        Assert.Equal(["ID", "Value"], alpha.Document.Columns.Select(column => column.Name));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Good))), alpha.Sha256);
        Assert.Equal(BundledDefinitions.Origin + "definitions/Alpha.dbd", alpha.SourceUrl);
        Assert.Same(alpha, provider.Require(Id(provider, "Alpha"))); Assert.Equal(1, provider.Definitions.Reads);
        // The second file sits after the first in the stream; reading it skips the first's bytes exactly.
        Assert.Equal(["ID", "Scale"], provider.Definitions[Id(provider, "Beta")].Document.Columns.Select(column => column.Name));
        Assert.Equal(2, provider.Definitions.Reads);
    }

    [Fact]
    public async Task Callers_asking_for_one_table_at_once_share_one_read()
    {
        var provider = await Bundle.Of(("Alpha", Good), ("Beta", Other)).LoadAsync();
        var id = Id(provider, "Beta"); using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { start.Wait(); return provider.Definitions[id]; })).ToArray();
        start.Set(); var results = await Task.WhenAll(readers);
        Assert.All(results, result => Assert.Same(results[0], result)); Assert.Equal(1, provider.Definitions.Reads);
    }

    [Fact]
    public async Task The_cache_keeps_the_most_recently_used_definitions_within_its_entry_bound()
    {
        var provider = await Bundle.Of(("A", Good), ("B", Good), ("C", Good)).LoadAsync(new(MaxEntries: 2));
        string a = Id(provider, "A"), b = Id(provider, "B"), c = Id(provider, "C");
        var first = provider.Definitions[a]; _ = provider.Definitions[b]; _ = provider.Definitions[a];
        _ = provider.Definitions[c];
        // B was least recently used, so it went; A stayed.
        Assert.Equal(2, provider.Definitions.CachedCount); Assert.Equal(3, provider.Definitions.Reads);
        Assert.Same(first, provider.Definitions[a]); Assert.Equal(3, provider.Definitions.Reads);
        _ = provider.Definitions[b]; _ = provider.Definitions[c];
        Assert.Equal(5, provider.Definitions.Reads); Assert.Equal(2, provider.Definitions.CachedCount);
        // A was evicted by now; read again it is the same bytes and parse in a new instance.
        var again = provider.Definitions[a];
        Assert.Equal(6, provider.Definitions.Reads); Assert.NotSame(first, again); Assert.Equal(first.Document.Columns, again.Document.Columns);
    }

    [Fact]
    public async Task The_cache_bounds_the_source_bytes_it_holds_and_always_keeps_the_latest()
    {
        var length = Encoding.UTF8.GetByteCount(Good);
        var provider = await Bundle.Of(("A", Good), ("B", Good), ("C", Good)).LoadAsync(new(MaxSourceBytes: length * 2));
        foreach (var name in new[] { "A", "B", "C" }) _ = provider.Definitions[Id(provider, name)];
        Assert.Equal(2, provider.Definitions.CachedCount); Assert.Equal(length * 2, provider.Definitions.CachedSourceBytes);
        var tiny = await Bundle.Of(("A", Good), ("B", Good)).LoadAsync(new(MaxSourceBytes: 1));
        _ = tiny.Definitions[Id(tiny, "A")]; _ = tiny.Definitions[Id(tiny, "B")];
        Assert.Equal(1, tiny.Definitions.CachedCount); Assert.Equal(length, tiny.Definitions.CachedSourceBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BundledDefinitionCache(new Dictionary<string, BundledDefinitionFile>(), () => Stream.Null, new(MaxEntries: 0)));
    }

    [Fact]
    public async Task A_definition_that_fails_makes_only_its_own_table_unusable_and_says_why()
    {
        var bundle = Bundle.Of([("Alpha", Good), ("Empty", Placeholder), ("Wide", BadBlock), ("Tampered", Other), ("Beta", Other)],
            (name, sha) => name == "Tampered" ? new string('0', 64) : sha);
        var provider = await bundle.LoadAsync();
        var empty = Assert.Throws<DefinitionException>(() => provider.Definitions[Id(provider, "Empty")]);
        Assert.Equal("DEFINITION_UNSUPPORTED", empty.Code);
        Assert.Equal("VibeMiner can’t read the pinned Empty definition (DBD_EMPTY), so this table can’t open. The definition declares no columns: " +
            "WoWDBDefs lists the table but doesn't describe it yet. Other tables still open.", empty.Message);
        var wide = Assert.Throws<DefinitionException>(() => provider.Require(Id(provider, "Wide")));
        Assert.Equal("DEFINITION_UNSUPPORTED", wide.Code); Assert.Contains("(DBD_UNSUPPORTED)", wide.Message);
        Assert.False(provider.TryGet(Id(provider, "Tampered"), out var none, out var tampered)); Assert.Null(none);
        Assert.Equal("DEFINITION_CORRUPT", tampered!.Code); Assert.Contains("Tampered definition doesn’t match its pinned SHA-256", tampered.Message);
        // Failures are remembered like definitions, and each caller gets its own exception.
        Assert.NotSame(empty, Assert.Throws<DefinitionException>(() => provider.Definitions[Id(provider, "Empty")]));
        Assert.Equal(3, provider.Definitions.Reads);
        // The tables around them still read, including the one after the tampered file in the stream.
        Assert.Equal(["ID", "Value"], provider.Definitions[Id(provider, "Alpha")].Document.Columns.Select(column => column.Name));
        Assert.Equal(["ID", "Scale"], provider.Definitions[Id(provider, "Beta")].Document.Columns.Select(column => column.Name));
        Assert.All(provider.Tables, entry => Assert.True(entry.Bundled));
        var missing = Assert.Throws<DefinitionException>(() => provider.Definitions["deadbeef"]);
        Assert.Equal("DEFINITION_NOT_BUNDLED", missing.Code);
        Assert.False(provider.TryGet("deadbeef", out _, out var notBundled)); Assert.Null(notBundled);
    }

    [Fact]
    public async Task Column_names_come_from_one_pass_that_reads_no_definition_into_the_cache()
    {
        var provider = await Bundle.Of([("Alpha", Good), ("Empty", Placeholder), ("Wide", BadBlock), ("Tampered", Other)],
            (name, sha) => name == "Tampered" ? new string('0', 64) : sha).LoadAsync();
        var names = await provider.Definitions.ColumnNamesAsync(default);
        Assert.Equal(["ID", "Value"], names[Id(provider, "Alpha")]);
        // A block the full reader refuses doesn't hide its columns; a placeholder or a tampered file has none.
        Assert.Equal(["ID", "Wide"], names[Id(provider, "Wide")]);
        Assert.False(names.ContainsKey(Id(provider, "Empty"))); Assert.False(names.ContainsKey(Id(provider, "Tampered")));
        Assert.Equal(0, provider.Definitions.Reads); Assert.Equal(0, provider.Definitions.CachedCount);
        Assert.Same(names, await provider.Definitions.ColumnNamesAsync(default));
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("length")]
    [InlineData("unnamed")]
    [InlineData("decoded")]
    [InlineData("repeated")]
    [InlineData("oversized")]
    [InlineData("revision")]
    [InlineData("compression")]
    public async Task A_stream_or_sources_list_that_does_not_verify_fails_the_whole_provider(string damage)
    {
        var bundle = Bundle.Of(("Alpha", Good), ("Beta", Other));
        var sources = JsonNodeOf(bundle.Sources);
        var definitions = sources["definitions"]!; var files = definitions["files"]!.AsArray();
        var stream = bundle.Stream;
        switch (damage)
        {
            case "stream": stream = [.. stream]; stream[^1] ^= 1; break;
            case "length": stream = [.. stream, 0]; break;
            case "unnamed": files[1]!["path"] = "definitions/Gamma.dbd"; break;
            case "decoded": definitions["decodedLength"] = definitions["decodedLength"]!.GetValue<long>() + 1; break;
            case "repeated": files[1]!["path"] = "definitions/Alpha.dbd"; break;
            case "oversized": files[0]!["byteLength"] = BundledDefinitions.MaxDefinitionBytes + 1; break;
            case "revision": sources["revision"] = new string('1', 40); break;
            case "compression": definitions["compression"] = "gzip"; break;
        }
        var failure = await Assert.ThrowsAsync<DefinitionException>(() => BundledDefinitions.CreateAsync(bundle.Manifest,
            Encoding.UTF8.GetBytes(sources.ToJsonString()), () => new MemoryStream(stream, writable: false), new(), default));
        Assert.Equal("DEFINITION_PROVIDER_INVALID", failure.Code);
    }

    [Fact]
    public void A_manifest_name_shared_by_two_tables_is_refused() =>
        Assert.Equal("DEFINITION_PROVIDER_INVALID", Assert.Throws<DefinitionException>(() =>
            BundledDefinitions.ParseManifest("[{\"tableHash\":\"00000001\",\"tableName\":\"A\"},{\"tableHash\":\"00000002\",\"tableName\":\"A\"}]"u8.ToArray())).Code);

    [Fact]
    public async Task A_manifest_entry_without_a_listed_file_stays_not_bundled()
    {
        var bundle = Bundle.Of(("Alpha", Good));
        var manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bundle.Manifest).TrimEnd(']') + ",{\"tableName\":\"Gamma\",\"tableHash\":\"0000000F\"}]");
        var provider = await BundledDefinitions.CreateAsync(manifest, bundle.Sources, () => new MemoryStream(bundle.Stream, writable: false), new(), default);
        Assert.Equal([true, false], provider.Tables.Select(entry => entry.Bundled));
        Assert.Equal("DEFINITION_NOT_BUNDLED", Assert.Throws<DefinitionException>(() => provider.Definitions["0000000f"]).Code);
    }

    [Fact]
    public async Task Every_pinned_definition_is_bundled_and_only_the_seven_placeholders_fail()
    {
        var provider = await BundledDefinitions.OpenAsync(default);
        Assert.Equal(1346, provider.Definitions.Count); Assert.Equal(PinnedDefinitionFiles.Names.Order(StringComparer.Ordinal),
            provider.Tables.Select(entry => entry.Name!).Order(StringComparer.Ordinal));
        var failures = new List<(string Name, string Code)>();
        foreach (var entry in provider.Tables)
        {
            Assert.True(entry.Bundled);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PinnedDefinitionFiles.Bytes(entry.Name!))), provider.Definitions.File(entry.TableId)!.Sha256);
            if (!provider.TryGet(entry.TableId, out _, out var failure)) failures.Add((entry.Name!, failure!.Code));
            Assert.InRange(provider.Definitions.CachedCount, 1, new BundledDefinitionCacheLimits().MaxEntries);
            Assert.InRange(provider.Definitions.CachedSourceBytes, 1, new BundledDefinitionCacheLimits().MaxSourceBytes);
        }
        // Upstream placeholders: just "COLUMNS", for tables WoWDBDefs knows of but doesn't describe.
        Assert.Equal(["Cfg_Languages", "Cfg_Realms", "GarrMissionReward", "MobileStrings", "SpellTooltip", "Treasure", "VW_MobileSpell"],
            failures.Select(failure => failure.Name).Order(StringComparer.Ordinal));
        Assert.All(failures, failure => { Assert.Equal("DEFINITION_UNSUPPORTED", failure.Code); Assert.Equal("COLUMNS\n\n", PinnedDefinitionFiles.Text(failure.Name)); });
        // The seven with LAYOUT-only blocks parse now, and their other blocks select builds as before.
        foreach (var name in new[] { "Achievement", "AlliedRace", "AlliedRaceRacialAbility", "AreaAssignment", "WorldMapTransforms", "WorldSafeLocs", "WorldStateUI" })
        {
            var document = provider.Definitions[Id(provider, name)].Document;
            Assert.Contains(document.Blocks, block => block.Builds.Count == 0 && block.LayoutHashes.Count > 0);
            Assert.Contains(document.Blocks, block => block.Builds.Count > 0);
        }
        // Column search covers every readable definition.
        var names = await provider.Definitions.ColumnNamesAsync(default);
        Assert.Equal(1339, names.Count); Assert.Contains("Ui_unlockAchievementID", names[Id(provider, "AlliedRace")]);
    }

    // Definition-level part of the legacy catalogue search test (the search request itself is not in layer 5).
    [Fact]
    public async Task Catalogue_search_finds_a_formerly_unbundled_table_by_a_column_name()
    {
        var provider = await BundledDefinitions.OpenAsync(default);
        var names = await provider.Definitions.ColumnNamesAsync(default);
        Assert.Equal(["AlliedRace"], names.Where(pair => pair.Value.Contains("Ui_unlockSecondaryAchievementID", StringComparer.OrdinalIgnoreCase))
            .Select(pair => provider.Tables.Single(entry => entry.TableId == pair.Key).Name));
    }

    // Definition-level part of the legacy exact-build tests: each build is listed by the block named here. The authored
    // DB2 bytes and row reads stay in Backend.Tests until the table data layer exists.
    [Theory]
    [InlineData("AlliedRace", "11.0.0.53687", "A21C7C29")]
    [InlineData("ArmorLocation", "11.0.0.53687", "6FA608FB")]
    [InlineData("AltMinimap", "11.0.0.53687", "459AD0B9")]
    [InlineData("BattlePetXP", "12.1.7.70424", "613380C2")]
    public async Task A_formerly_unbundled_table_opens_by_exact_build_from_authored_bytes(string name, string build, string layout)
    {
        var provider = await BundledDefinitions.OpenAsync(default);
        var entry = provider.Tables.Single(value => value.Name == name);
        Assert.NotNull(entry.Db2FileDataId);
        var block = provider.Require(entry.TableId).Document.SelectBuild(build).Blocks.Single();
        Assert.Equal(layout, block.LayoutHashes.Single());
        Assert.Single(block.Fields, field => field.IsId);
    }

    [Fact]
    public async Task A_placeholder_table_stops_before_any_download_and_says_why()
    {
        var provider = await BundledDefinitions.OpenAsync(default);
        var failure = Assert.Throws<DefinitionException>(() => provider.Require(Id(provider, "Treasure")));
        Assert.Equal("DEFINITION_UNSUPPORTED", failure.Code); Assert.Contains("Treasure", failure.Message);
        // The table that shares the build is unaffected.
        Assert.Equal("A21C7C29", provider.Require(Id(provider, "AlliedRace")).Document.SelectBuild("11.0.0.53687").Blocks.Single().LayoutHashes.Single());
    }

    [Fact]
    public async Task The_pinned_provider_is_loaded_once_and_shared()
    {
        var first = await BundledDefinitions.OpenAsync(default);
        Assert.Same(first, await BundledDefinitions.OpenAsync(default));
        Assert.Equal(new DefinitionProviderInfo("WoWDBDefs contributors", BundledDefinitions.Revision, BundledDefinitions.ManifestSha256,
            "CC-BY-SA-4.0", $"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledDefinitions.Revision}/manifest.json"), first.Info);
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeOf(byte[] bytes) => System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
}
