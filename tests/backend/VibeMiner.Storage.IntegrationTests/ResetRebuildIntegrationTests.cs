using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Builds.Formats;
using VibeMiner.Builds.Tests.Formats;
using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Tests.Tables;
using VibeMiner.Generated;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>
/// The whole reset chain (#195): the real object store, catalog store and generated store in a temporary data folder, the real
/// layer 2 source over the fake CDN's HTTP front end and a scripted version service, and layers 3 to 5 on top. A first run
/// refreshes, opens a synthetic build and reads its tables; then catalog-v1/ and generated-v1/ are deleted as <c>vm reset</c>
/// does, and a second run over freshly opened stores must read the same tables while asking only for versions, cdns and the
/// summary. Synthetic data only.
/// </summary>
public sealed class ResetRebuildIntegrationTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Product = "wow";
    // The CLI's folder names (RealStores.CatalogFolder, ProfileReset.DefaultTargets); this project can't reference the CLI.
    private const string CatalogFolder = "catalog-v1", CatalogFile = "catalog.sqlite3";
    private static readonly ObjectStoreOptions Quick = new() { CommitDelay = TimeSpan.FromMilliseconds(5) };

    private readonly string data = Path.Combine(Path.GetTempPath(), "vm-reset-chain-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCdn cdn = new() { Time = new ManualTime(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)) };
    private readonly VersionService versions = new();

    public Task InitializeAsync() { Directory.CreateDirectory(data); return Task.CompletedTask; }
    public Task DisposeAsync()
    {
        try { Directory.Delete(data, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static readonly ScenarioColumn[] Columns = [ScenarioColumn.Id(), ScenarioColumn.Int("Level"), ScenarioColumn.Text("Name")];

    /// <summary>Three synthetic tables: two in one archive, one loose.</summary>
    private static readonly (uint Id, string Name, bool Archived, ScenarioRow[] Rows)[] Tables =
    [
        (900_001, "SynthAlpha", true, [new(1, ["1", "10", "alpha one"]), new(2, ["2", "20", "alpha two"])]),
        (900_002, "SynthBeta", true, [new(5, ["5", "-3", "beta"])]),
        (900_003, "SynthGamma", false, [new(7, ["7", "70", "gamma seven"]), new(8, ["8", "80", "gamma eight"]), new(9, ["9", "90", ""])]),
    ];

    [Fact]
    public async Task AResetRebuildsTheSameTablesAskingOnlyTheVersionService()
    {
        var build = PublishSyntheticBuild();

        var first = await RunAsync();
        Assert.Equal(build.BuildConfig, first.Build.BuildConfig);
        Assert.Equal(build.CdnConfig, first.Build.CdnConfig);
        Assert.Equal(Tables.Length, first.Tables.Count);
        Assert.All(first.Tables, t => Assert.Equal(TableState.Ready, t.State));
        foreach (var table in Tables)
            Assert.Equal(table.Rows.Select(r => string.Join('|', r.Values)), first.Rows[table.Id]);
        Assert.Contains(cdn.Requests, r => r.Kind == CdnObjectKind.Data && r.Offset is not null); // archived tables came by range
        Assert.Contains(cdn.Requests, r => r.Kind == CdnObjectKind.Data && r.Offset is null);     // the loose one whole
        Assert.True(Directory.Exists(Path.Combine(data, GeneratedStore.DefaultFolderName)));
        Assert.True(File.Exists(Path.Combine(data, CatalogFolder, CatalogFile)));

        // vm reset: wipe the catalog and generated stores, keep cdn-store-v1/.
        var objectsBefore = await CountObjectsAsync();
        Directory.Delete(Path.Combine(data, CatalogFolder), recursive: true);
        Directory.Delete(Path.Combine(data, GeneratedStore.DefaultFolderName), recursive: true);
        cdn.ClearRequests();
        versions.Clear();

        var second = await RunAsync();
        Assert.Empty(cdn.Requests);
        Assert.NotEmpty(versions.Paths);
        Assert.All(versions.Paths, p => Assert.Contains(p, VersionService.Allowed));
        Assert.Equal(first.Build, second.Build);
        Assert.Equal(first.Tables, second.Tables);
        Assert.Equal(first.Rows, second.Rows);
        // Nothing new but the three history records of the refresh; their reply documents are the bytes already stored.
        var objectsAfter = await CountObjectsAsync();
        Assert.Equal(objectsBefore.HistoryRecords + 3, objectsAfter.HistoryRecords);
        Assert.Equal(objectsBefore.Objects - objectsBefore.HistoryRecords, objectsAfter.Objects - objectsAfter.HistoryRecords);
        Assert.True(Directory.Exists(Path.Combine(data, GeneratedStore.DefaultFolderName)));
    }

    private sealed record Run(BuildRef Build, IReadOnlyList<(uint Id, string? Name, TableState State, long? Rows)> Tables,
        IReadOnlyDictionary<uint, IReadOnlyList<string>> Rows)
    {
        public bool Equals(Run? other) => other is not null && Build == other.Build && Tables.SequenceEqual(other.Tables);
        public override int GetHashCode() => Build.GetHashCode();
    }

    /// <summary>
    /// One run over freshly opened stores, as <c>vm builds refresh</c> then <c>vm tables states</c>: refresh the summary and the
    /// product, take its current build, list and check every table, and read every row.
    /// </summary>
    private async Task<Run> RunAsync()
    {
        await using var store = await ObjectStore.OpenAsync(Path.Combine(data, ObjectStore.DefaultFolderName), Quick);
        using var http = new HttpClient(cdn.Handler());
        using var versionHttp = new HttpClient(versions);
        var source = new ObjectSource(store, store, store, null, new CdnClient(http, new CdnClientOptions { Time = cdn.Time }),
            [new VersionServiceObservations(new VersionClient(versionHttp))], ObjectVerification.Default);

        BuildRef build;
        await using (var catalog = await Catalog.OpenAsync(source, Path.Combine(data, CatalogFolder, CatalogFile), null, None))
        {
            Assert.Equal(ObjectState.Present, (await catalog.RefreshSummaryAsync(None)).Summary.State);
            var refresh = await catalog.RefreshAsync(Product, None);
            Assert.Equal(ObjectState.Present, refresh.Versions.State);
            Assert.Equal(ObjectState.Present, refresh.Cdns.State);
            var current = Assert.Single(refresh.List.Builds, b => b.Current);
            var region = Assert.Single(current.Regions, r => r.Current);
            build = new BuildRef(current.Product, current.BuildConfig, region.CdnConfig, region.Region);
        }

        await using var generated = await GeneratedStore.OpenAsync(Path.Combine(data, GeneratedStore.DefaultFolderName), cancellationToken: None);
        var definitions = new TableDefinitions(await SyntheticTables.ProviderAsync(
            [.. Tables.Select(t => new SyntheticTable(t.Id, t.Name, SyntheticTables.Dbd(Columns), null))]), null, null);
        var builds = new BuildOpener(source);
        var service = new TablesService(new FileSystem(builds, source), definitions, new GeneratedTables(generated),
            new TablesOptions(async (b, ct) => (await builds.OpenAsync(b, ct)).State == BuildOpenState.Open ? SyntheticTables.Version : null));

        var opened = await service.ForAsync(build, "enUS", None);
        Assert.Equal(FilesOpenState.Open, opened.State);
        await using var tables = opened.Tables!;
        var listed = new List<TableEntry>();
        await foreach (var entry in tables.ListAsync(TableListFilter.All, TablePriority.Background, None)) listed.Add(entry);
        var states = new List<(uint, string?, TableState, long?)>();
        var rows = new Dictionary<uint, IReadOnlyList<string>>();
        foreach (var entry in listed.OrderBy(e => e.FileDataId))
        {
            var checkedEntry = await tables.CheckAsync(TableRef.Id(entry.FileDataId), TablePriority.Background, None);
            states.Add((checkedEntry.FileDataId, checkedEntry.Name, checkedEntry.State, checkedEntry.Counts?.ReadableRows));
            var page = await tables.PageAsync(new TablePageRequest(TableRef.Id(entry.FileDataId)), TablePriority.Interactive, None);
            rows[entry.FileDataId] = [.. page.Rows.Select(r => string.Join('|', r.Row.Values))];
        }
        return new(build, states, rows);
    }

    private async Task<StoreStats> CountObjectsAsync()
    {
        await using var store = await ObjectStore.OpenReadOnlyAsync(Path.Combine(data, ObjectStore.DefaultFolderName), Quick);
        return await store.StatsAsync(None);
    }

    // ---------------------------------------------------------------- the synthetic build on the fake CDN

    private BuildRef PublishSyntheticBuild()
    {
        var files = Tables.Select(t =>
        {
            var (bytes, hole) = SyntheticTables.Wdc5(Columns, t.Rows, null, t.Id);
            Assert.Null(hole);
            return (t.Id, t.Name, t.Archived, Blte: BlteWriter.Chunked(BlteWriter.Z(bytes)));
        }).OrderBy(f => f.Id).ToArray();

        var rootBytes = RootWriter.Build(2, 24, new RootBlock(RootWriter.Deltas([.. files.Select(f => f.Id)]), 0x2, 0,
            Names: [.. files.Select(f => RootNameHash.Of($"dbfilesclient/{f.Name!.ToLowerInvariant()}.db2"))],
            Keys: [.. files.Select(f => f.Blte.CKey)]));
        var root = BlteWriter.Chunked(BlteWriter.Z(rootBytes));
        Serve(CdnObjectKind.Data, root.EKey, root.Encoded);

        var encodingBytes = EncodingWriter.FromFiles(files.Select(f => (f.Blte.CKey, (long)f.Blte.Content.Length, f.Blte.EKey, (long)f.Blte.Encoded.Length))
            .Append((root.CKey, root.Content.Length, root.EKey, root.Encoded.Length)));
        var encoding = BlteWriter.Chunked(BlteWriter.Z(encodingBytes));
        Serve(CdnObjectKind.Data, encoding.EKey, encoding.Encoded);

        // One archive: a gap, then each archived table at its offset. Its name is its index's key.
        using var archive = new MemoryStream();
        var rows = new List<(CdnKey EKey, uint Size, uint Offset)>();
        foreach (var f in files.Where(f => f.Archived))
        {
            archive.Write(new byte[64]);
            rows.Add((f.Blte.EKey, (uint)f.Blte.Encoded.Length, (uint)archive.Length));
            archive.Write(f.Blte.Encoded);
        }
        var (index, archiveKey) = ArchiveIndex([.. rows.OrderBy(r => r.EKey)]);
        Serve(CdnObjectKind.Index, archiveKey, index);
        Serve(CdnObjectKind.Data, archiveKey, archive.ToArray());
        foreach (var f in files.Where(f => !f.Archived)) Serve(CdnObjectKind.Data, f.Blte.EKey, f.Blte.Encoded);

        var buildConfig = Encoding.ASCII.GetBytes($"# Build Configuration\n\nroot = {root.CKey.Hex}\ninstall = {new string('1', 32)}\n" +
            $"encoding = {encoding.CKey.Hex} {encoding.EKey.Hex}\nencoding-size = {encoding.Content.Length} {encoding.Encoded.Length}\n" +
            "build-name = WOW-4patch1.2.3_Synthetic\n");
        var cdnConfig = Encoding.ASCII.GetBytes($"# CDN Configuration\n\narchives = {archiveKey.Hex}\n");
        var buildKey = new CdnKey(MD5.HashData(buildConfig));
        var cdnKey = new CdnKey(MD5.HashData(cdnConfig));
        Serve(CdnObjectKind.Config, buildKey, buildConfig);
        Serve(CdnObjectKind.Config, cdnKey, cdnConfig);
        versions.Versions = Encoding.ASCII.GetBytes(
            "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16\n" +
            $"## seqn = 1\nus|{buildKey.Hex}|{cdnKey.Hex}||4|1.2.3.4|\n");
        return new(Product, buildKey.Hex, cdnKey.Hex, "us");
    }

    private void Serve(CdnObjectKind kind, CdnKey key, byte[] bytes) => cdn.Add(kind, key, bytes);

    /// <summary>A one-page archive index (24-byte rows: key, size, offset) with its TOC and footer; the key is the footer's MD5.</summary>
    private static (byte[] Bytes, CdnKey Key) ArchiveIndex(IReadOnlyList<(CdnKey EKey, uint Size, uint Offset)> rows)
    {
        const int page = 4096, footerBytes = 28, record = 24;
        Assert.InRange(rows.Count, 1, page / record);
        var bytes = new byte[page + 24 + footerBytes];
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].EKey.WriteTo(bytes.AsSpan(i * record));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * record + 16), rows[i].Size);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * record + 20), rows[i].Offset);
        }
        var toc = bytes.AsSpan(page, 24);
        rows[^1].EKey.WriteTo(toc);
        MD5.HashData(bytes.AsSpan(0, page)).AsSpan(0, 8).CopyTo(toc[16..]);
        var footer = bytes.AsSpan(bytes.Length - footerBytes);
        MD5.HashData(toc).AsSpan(0, 8).CopyTo(footer);
        footer[8] = 1; footer[11] = 4; footer[12] = record - 20; footer[13] = 4; footer[14] = 16; footer[15] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], (uint)rows.Count);
        MD5.HashData(footer[8..]).AsSpan(0, 8).CopyTo(footer[20..]);
        return (bytes, new CdnKey(MD5.HashData(footer)));
    }

    /// <summary>The version service for one synthetic product: summary, versions and cdns; anything else is a 404. Logs each path.</summary>
    private sealed class VersionService : HttpMessageHandler
    {
        public static readonly string[] Allowed = ["/v2/summary", $"/v2/products/{Product}/versions", $"/v2/products/{Product}/cdns"];
        private static readonly byte[] Summary = "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 1\nwow|1|\nwow|1|cdn\n"u8.ToArray();
        private static readonly byte[] Cdns = Encoding.ASCII.GetBytes("Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0\n" +
            $"## seqn = 1\nus|tpr/wow|{Scenario.L3} {Scenario.Us}||tpr/configs/data\n");
        private readonly List<string> paths = [];
        public byte[] Versions { get; set; } = [];
        public IReadOnlyList<string> Paths { get { lock (paths) return [.. paths]; } }
        public void Clear() { lock (paths) paths.Clear(); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (paths) paths.Add(path);
            var body = path == Allowed[0] ? Summary : path == Allowed[1] ? Versions : path == Allowed[2] ? Cdns : null;
            var response = new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body ?? "404"u8.ToArray()), RequestMessage = request,
            };
            response.Content.Headers.ContentType = new("text/plain");
            return Task.FromResult(response);
        }
    }
}
