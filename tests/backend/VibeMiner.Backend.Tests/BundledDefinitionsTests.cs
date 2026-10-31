using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;
using R = VibeMiner.Backend.Tests.TableAdmissionRevisionTests;

namespace VibeMiner.Backend.Tests;

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
                provider = "WoWDBDefs contributors", revision = BundledTableDefinitions.Revision, definitionLicense = "CC-BY-SA-4.0",
                sourceBase = BundledTableDefinitions.Origin, resources = Array.Empty<object>(),
                definitions = new { path = "definitions.br", compression = "brotli", sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
                    byteLength = stream.Length, decodedLength = decoded.Length, files = listed }
            });
            return new(Encoding.UTF8.GetBytes(manifest), sources, stream);
        }
        internal Task<BundledTableDefinitions> LoadAsync(BundledDefinitionCacheLimits? limits = null) =>
            BundledTableDefinitions.CreateAsync(Manifest, Sources, () => new MemoryStream(Stream, writable: false), limits ?? new(), default);
    }

    private static string Id(BundledTableDefinitions provider, string name) => provider.Entries.Single(entry => entry.Name == name).TableId;

    [Fact]
    public async Task Loading_reads_no_definition_and_each_is_read_once_then_cached()
    {
        var provider = await Bundle.Of(("Alpha", Good), ("Beta", Other)).LoadAsync();
        Assert.Equal(0, provider.Definitions.Reads); Assert.Equal(0, provider.Definitions.CachedCount);
        Assert.All(provider.Entries, entry => Assert.Equal("bundled", entry.DefinitionState));
        var alpha = provider.Definitions[Id(provider, "Alpha")];
        Assert.Equal(1, provider.Definitions.Reads);
        Assert.Equal(["ID", "Value"], alpha.Document.Columns.Select(column => column.Name));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Good))), alpha.Sha256);
        Assert.Equal(BundledTableDefinitions.Origin + "definitions/Alpha.dbd", alpha.SourceUrl);
        Assert.Same(alpha, provider.Definitions[Id(provider, "Alpha")]); Assert.Equal(1, provider.Definitions.Reads);
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
        var empty = Assert.Throws<DiscoveryException>(() => provider.Definitions[Id(provider, "Empty")]);
        Assert.Equal("DEFINITION_UNSUPPORTED", empty.Code);
        Assert.Equal("VibeMiner can’t read the pinned Empty definition (DBD_EMPTY), so this table can’t open. The definition declares no columns: " +
            "WoWDBDefs lists the table but doesn't describe it yet. Other tables still open.", empty.Message);
        var wide = Assert.Throws<DiscoveryException>(() => provider.Definitions.Require(Id(provider, "Wide")));
        Assert.Equal("DEFINITION_UNSUPPORTED", wide.Code); Assert.Contains("(DBD_UNSUPPORTED)", wide.Message);
        Assert.False(provider.Definitions.TryGet(Id(provider, "Tampered"), out var none, out var tampered)); Assert.Null(none);
        Assert.Equal("DEFINITION_CORRUPT", tampered!.Code); Assert.Contains("Tampered definition doesn’t match its pinned SHA-256", tampered.Message);
        // Failures are remembered like definitions, and each caller gets its own exception.
        Assert.NotSame(empty, Assert.Throws<DiscoveryException>(() => provider.Definitions[Id(provider, "Empty")]));
        Assert.Equal(3, provider.Definitions.Reads);
        // The tables around them still read, including the one after the tampered file in the stream.
        Assert.Equal(["ID", "Value"], provider.Definitions[Id(provider, "Alpha")].Document.Columns.Select(column => column.Name));
        Assert.Equal(["ID", "Scale"], provider.Definitions[Id(provider, "Beta")].Document.Columns.Select(column => column.Name));
        Assert.All(provider.Entries, entry => Assert.Equal("bundled", entry.DefinitionState));
        var missing = Assert.Throws<DiscoveryException>(() => provider.Definitions["deadbeef"]);
        Assert.Equal("DEFINITION_NOT_BUNDLED", missing.Code);
        Assert.False(provider.Definitions.TryGet("deadbeef", out _, out var notBundled)); Assert.Null(notBundled);
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
            case "oversized": files[0]!["byteLength"] = BundledTableDefinitions.MaxDefinitionBytes + 1; break;
            case "revision": sources["revision"] = new string('1', 40); break;
            case "compression": definitions["compression"] = "gzip"; break;
        }
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => BundledTableDefinitions.CreateAsync(bundle.Manifest,
            Encoding.UTF8.GetBytes(sources.ToJsonString()), () => new MemoryStream(stream, writable: false), new(), default));
        Assert.Equal("DEFINITION_PROVIDER_INVALID", failure.Code);
    }

    [Fact]
    public void A_manifest_name_shared_by_two_tables_is_refused() =>
        Assert.Equal("DEFINITION_PROVIDER_INVALID", Assert.Throws<DiscoveryException>(() =>
            BundledTableDefinitions.ParseManifest("[{\"tableHash\":\"00000001\",\"tableName\":\"A\"},{\"tableHash\":\"00000002\",\"tableName\":\"A\"}]"u8.ToArray())).Code);

    [Fact]
    public async Task A_manifest_entry_without_a_listed_file_stays_not_bundled()
    {
        var bundle = Bundle.Of(("Alpha", Good));
        var manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bundle.Manifest).TrimEnd(']') + ",{\"tableName\":\"Gamma\",\"tableHash\":\"0000000F\"}]");
        var provider = await BundledTableDefinitions.CreateAsync(manifest, bundle.Sources, () => new MemoryStream(bundle.Stream, writable: false), new(), default);
        Assert.Equal(["bundled", "notBundled"], provider.Entries.Select(entry => entry.DefinitionState));
        Assert.Equal("DEFINITION_NOT_BUNDLED", Assert.Throws<DiscoveryException>(() => provider.Definitions["0000000f"]).Code);
    }

    [Fact]
    public async Task Every_pinned_definition_is_bundled_and_only_the_seven_placeholders_fail()
    {
        var provider = await TablesCatalog.Provider.Value;
        Assert.Equal(1346, provider.Definitions.Count); Assert.Equal(PinnedDefinitionFiles.Names.Order(StringComparer.Ordinal),
            provider.Entries.Select(entry => entry.Name!).Order(StringComparer.Ordinal));
        var failures = new List<(string Name, string Code)>();
        foreach (var entry in provider.Entries)
        {
            Assert.Equal("bundled", entry.DefinitionState);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PinnedDefinitionFiles.Bytes(entry.Name!))), provider.Definitions.File(entry.TableId)!.Sha256);
            if (!provider.Definitions.TryGet(entry.TableId, out _, out var failure)) failures.Add((entry.Name!, failure!.Code));
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

    [Fact]
    public async Task Catalogue_search_finds_a_formerly_unbundled_table_by_a_column_name()
    {
        await using var h = await Opened.Create("11.0.0.53687", "AlliedRace");
        var page = await new TablesCatalog(h.Catalog).ReadCatalogAsync(new("wow", h.BuildId, "us", "Ui_unlockSecondaryAchievementID"), default);
        Assert.Equal(["AlliedRace"], page.Entries.Select(entry => entry.Name));
    }

    // ---- Formerly unbundled tables open from authored DB2 bytes (no game content) ----

    // AlliedRace is one of the seven the parser refused before #144 (LAYOUT-only blocks); ArmorLocation stores floats;
    // AltMinimap keeps its ID inline; BattlePetXP is on a 12.1.7 build. Each build is listed by the block named here.
    [Theory]
    [InlineData("AlliedRace", "11.0.0.53687", "A21C7C29")]
    [InlineData("ArmorLocation", "11.0.0.53687", "6FA608FB")]
    [InlineData("AltMinimap", "11.0.0.53687", "459AD0B9")]
    [InlineData("BattlePetXP", "12.1.7.70424", "613380C2")]
    public async Task A_formerly_unbundled_table_opens_by_exact_build_from_authored_bytes(string name, string build, string layout)
    {
        await using var h = await Opened.Create(build, name);
        Assert.Equal(layout, h.Layout);
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal(layout, snapshot.LayoutHash); Assert.Null(snapshot.SchemaMatch);
        Assert.Equal(h.Definition.Sha256, snapshot.DefinitionSha256); Assert.Equal(BundledTableDefinitions.Revision, snapshot.ProviderRevision);
        Assert.Null(snapshot.FetchedDefinition); Assert.Equal("2", snapshot.RowCount);
        Assert.Equal(h.Definition.Document.SelectBuild(build).Blocks.Single().Fields.Select(field => field.Column.Name), snapshot.Columns.Select(column => column.Name));
        var rows = await h.Store.ReadRowsAsync(new("wow", h.Request.BuildIdentity, "us", h.Request.TableId, "enUS", snapshot.ArtifactId), default);
        Assert.Equal(["7", "8"], rows.Rows.Select(row => row.Id));
        Assert.Equal(h.Expected, rows.Rows.Select(row => (IReadOnlyList<string>)row.Values.ToArray()));
    }

    [Fact]
    public async Task A_placeholder_table_stops_before_any_download_and_says_why()
    {
        await using var h = await Opened.Create("11.0.0.53687", "AlliedRace");
        var provider = await TablesCatalog.Provider.Value; var treasure = Id(provider, "Treasure");
        var requests = h.Cdn.Requests;
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request with { TableId = treasure }, _ => Task.CompletedTask, default));
        Assert.Equal("DEFINITION_UNSUPPORTED", failure.Code); Assert.Contains("Treasure", failure.Message);
        Assert.Equal(requests, h.Cdn.Requests);
        // The table that shares the build is unaffected.
        Assert.Equal("2", (await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default)).RowCount);
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeOf(byte[] bytes) => System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();

    /// <summary>One opened build whose ROOT maps the named table's FileDataID to an authored WDC5 file with two rows.
    /// Every inline field is a 32-bit integer or float stored uncompressed, which is what the sample tables declare.</summary>
    private sealed class Opened : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        private VerifiedObjectStore objects = null!; private HttpClient client = null!;
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        internal R.Handler Cdn { get; private set; } = null!;
        internal TableDataStore Store { get; private set; } = null!;
        internal TableDataRequest Request { get; private set; } = null!;
        internal string BuildId => Request.BuildIdentity;
        internal BundledTableDefinition Definition { get; private set; } = null!;
        internal string Layout { get; private set; } = "";
        internal IReadOnlyList<IReadOnlyList<string>> Expected { get; private set; } = [];

        internal static async Task<Opened> Create(string build, string name)
        {
            var h = new Opened(); var fixture = InventoryFixture.Create(); var provider = await TablesCatalog.Provider.Value;
            var entry = provider.Entries.Single(value => value.Name == name);
            h.Definition = provider.Definitions[entry.TableId];
            var block = h.Definition.Document.SelectBuild(build).Blocks.Single(); h.Layout = block.LayoutHashes.Single();
            var (table, expected) = Table(entry.TableId, block, h.Layout); h.Expected = expected;
            var payload = R.Raw("table", table); var root = Root(uint.Parse(entry.Db2FileDataId!), payload.ContentKey); var en = R.Encoding([payload, root]);
            var config = Encoding.UTF8.GetBytes($"root = {root.ContentKey}\nencoding = {en.ContentKey} {en.EncodingKey}\nencoding-size = {en.Decoded.Length} {en.Encoded.Length}\n");
            var configKey = R.Md5(config); var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                { [InventoryFixture.Url("config", configKey)] = config, [InventoryFixture.Url("config", fixture.CdnKey)] = fixture.CdnConfig };
            foreach (var obj in new[] { payload, root, en }) responses.Add(InventoryFixture.Url("data", obj.EncodingKey), obj.Encoded);
            var prior = fixture.Discovery.Documents.Single(d => d.Role == "versions");
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(prior.Bytes).Replace(fixture.BuildKey, configKey, StringComparison.Ordinal).Replace("1.2.3.12345", build, StringComparison.Ordinal));
            var row = MetadataParser.Parse(bytes).Rows.Single(); var identity = BuildIdentity.Create("wow", row.Fields);
            var discovery = fixture.Discovery with { Documents = [prior with { Bytes = bytes, Sha256 = R.Sha(bytes) }, fixture.Discovery.Documents.Single(d => d.Role == "cdns")],
                Observations = [fixture.Discovery.Observations.Single() with { Identity = identity, OriginalFields = row.Fields }] };
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory.Path); h.objects = new(h.directory.Path);
            h.Cdn = new(responses); h.client = new(h.Cdn);
            h.Request = new("wow", identity.Id, "us", entry.TableId, "enUS");
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, discovery, "2026-10-31T00:00:00Z", default);
            using (var configurations = new ConfigurationWorkspacePreparer(h.Catalog, h.objects, h.client))
                await configurations.PrepareAsync(new("wow", identity.Id, "us"), _ => Task.CompletedTask, default);
            h.Store = new(h.Catalog, h.objects, h.client);
            return h;
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); directory.Dispose();
        }
    }

    /// <summary>A one-section WDC5 file (flags 0 with an inline ID, 4 with an ID list) holding rows 7 and 8, and the values
    /// each row should read back as, in the block's field order.</summary>
    private static (byte[] Bytes, IReadOnlyList<IReadOnlyList<string>> Expected) Table(string tableId, DbdBlock block, string layout)
    {
        var inline = block.Fields.Where(field => !field.NonInline).ToArray(); var n = inline.Length; var recordSize = n * 4;
        Assert.All(inline, field => Assert.True(field.ArrayLength == 1 && (field.Column.Family == "float" || field.Column.Family == "int" && field.IntegerBits == 32)));
        var id = block.Fields.Select((field, index) => (field, index)).Single(pair => pair.field.IsId);
        var idList = id.field.NonInline; uint[] ids = [7, 8];
        var start = 204 + 40 + n * 28; var body = new byte[start + 2 * recordSize + (idList ? 8 : 0)];
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), value);
        "WDC5"u8.CopyTo(body); Put(4, 5); "authored"u8.CopyTo(body.AsSpan(8));
        Put(136, 2); Put(140, (uint)n); Put(144, (uint)recordSize); Put(152, Convert.ToUInt32(tableId, 16)); Put(156, Convert.ToUInt32(layout, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(172), (ushort)(idList ? 4 : 0)); BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(174), (ushort)id.index);
        Put(176, (uint)n); Put(188, (uint)n * 24); Put(200, 1);
        Put(212, (uint)start); Put(216, 2); Put(228, idList ? 8u : 0u);
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(244 + i * 4 + 2), (ushort)(i * 4));
            var at = 244 + n * 4 + i * 24; BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), (ushort)(i * 32)); BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 2), 32);
        }
        var expected = new List<IReadOnlyList<string>>();
        for (var r = 0; r < 2; r++)
        {
            var values = new List<string>();
            foreach (var field in block.Fields)
            {
                if (field.IsId) { values.Add(ids[r].ToString(System.Globalization.CultureInfo.InvariantCulture)); continue; }
                var i = Array.IndexOf(inline, field); var at = start + r * recordSize + i * 4;
                if (field.Column.Family == "float") { BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(at), 1.5f + r); values.Add((1.5f + r).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                else { var value = (uint)(100 * (r + 1) + i); Put(at, value); values.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            }
            if (!idList) Put(start + r * recordSize + Array.IndexOf(inline, id.field) * 4, ids[r]);
            expected.Add(values);
        }
        if (idList) { Put(start + 2 * recordSize, ids[0]); Put(start + 2 * recordSize + 4, ids[1]); }
        return (body, expected);
    }

    private static R.SourceObject Root(uint fileDataId, string key)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write("TSFM"u8); w.Write(24u); w.Write(2u); w.Write(1u); w.Write(0u); w.Write(0u);
        w.Write(1u); w.Write(2u); w.Write(0x10000000u); w.Write(0u); w.Write((byte)0); w.Write(fileDataId); w.Write(Convert.FromHexString(key));
        return R.Raw("root", stream.ToArray());
    }
}
