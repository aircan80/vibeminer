using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Backend.Tests;

// All bytes are authored. No downloaded game fixture, external parser or network is used.
// Fresh catalog tests obtain Closure ONLY by running this real shared B/physical/BLTE/M2 pipeline.
internal sealed class CreatureGeometrySourcesFixture : IAsyncDisposable
{
    internal const uint ModelId = 777, SkinId = 778, TextureId = 779;
    internal const string Time = "2026-10-19T12:00:00.0000000+00:00";
    internal readonly CreaturesProjectionTests.Fixture Projection = new();
    internal readonly AcquisitionResources Resources = new(maximumCpu: 1, maximumBackgroundCpu: 1, maximumStagingBytes: 16 << 20, maximumBackgroundStagingBytes: 8 << 20);
    internal readonly GeometryPayloadBudget Payload = new();
    internal DiscoveryCatalog Catalog = null!;
    internal CapturedOpeningContext Opening = null!;
    internal CreatureDisplayMetadataRequest Request = null!;
    internal GeometryDisplayAuthorization Selection = null!;
    internal GeometrySourceServices Services = null!;
    internal FixtureFence Fence = null!;
    internal Handler Http = new();
    internal HttpClient Client = null!;
    internal AcquisitionCoordinator Shared = null!;
    internal AssetResolutionSessions Sessions = null!;
    internal CreaturesStore Creatures = null!;
    internal CreatureDisplayMetadataStore Displays = null!;
    internal TableDataService Tables = null!;
    internal GeometryPreparedSources? Prepared;
    internal GeometryProducerPayload? Producer;
    private IDisposable? capture;
    internal byte[] ModelEncoded = [], SkinEncoded = [], TextureEncoded = [];
    // Textured appearance (#63): an authored model/SKIN pair, one BLP and the display row's texture fields.
    internal sealed record Textured(AppearanceModels.Model Model, AppearanceModels.Batch[] Batches, int Variation = (int)TextureId, int Extended = 0,
        bool Encrypted = false, byte[]? Blp = null, int Sections = 1);
    internal static async Task<CreatureGeometrySourcesFixture> CreateAsync(bool empty = false, bool encryptedSkin = false, bool oversizedModel = false, bool ambiguousModel = false, bool missingModel = false, bool externalSkeleton = false,
        Textured? textured = null)
    {
        var f = new CreatureGeometrySourcesFixture();
        try { await f.InitializeAsync(empty, encryptedSkin, oversizedModel, ambiguousModel, missingModel, externalSkeleton, textured); return f; }
        catch { await f.DisposeAsync(); throw; }
    }
    private async Task InitializeAsync(bool empty, bool encryptedSkin, bool oversizedModel, bool ambiguousModel, bool missingModel, bool externalSkeleton, Textured? textured)
    {
        var model = Object("model", textured is null ? ModelBytes(externalSkeleton) : AppearanceModels.ModelBytes(textured.Model));
        var skin = Object("skin", textured is null ? SkinBytes(empty) : AppearanceModels.SkinBytes(textured.Batches, textured.Sections));
        var texture = Object("texture", textured?.Blp ?? AppearanceModels.Blp(8, 8, 4));
        if (textured?.Encrypted == true) { texture = texture with { Encoded = Encrypted(texture.Decoded.Length) }; texture = texture with { EncodingKey = Hash(texture.Encoded.AsSpan(0, 36)) }; }
        TextureEncoded = texture.Encoded;
        if (encryptedSkin) skin = skin with { Encoded = Encrypted(skin.Decoded.Length) };
        if (encryptedSkin) skin = skin with { EncodingKey = Hash(skin.Encoded.AsSpan(0, 36)) };
        var table = Object("table", "authored table source\n"u8.ToArray());
        var root = Object("root", Root(model, skin, table, ambiguousModel, missingModel, textured is null ? null : texture));
        var encoding = Object("encoding", EncodingManifest(textured is null ? [model, skin, table, root] : [model, skin, table, texture, root], oversizedModel));
        ModelEncoded = model.Encoded; SkinEncoded = skin.Encoded;
        var build = Encoding.UTF8.GetBytes($"root = {root.ContentKey}\nencoding = {encoding.ContentKey} {encoding.EncodingKey}\nencoding-size = {encoding.Decoded.Length} {encoding.Encoded.Length}\n");
        var cdn = "archives =\n"u8.ToArray(); var buildKey = Hash(build); var cdnKey = Hash(cdn);
        Http.Responses[InventoryFixture.Url("config", buildKey)] = build; Http.Responses[InventoryFixture.Url("config", cdnKey)] = cdn;
        foreach (var item in new[] { model, skin, table, root, encoding, texture }) Http.Responses[InventoryFixture.Url("data", item.EncodingKey)] = item.Encoded;
        Catalog = await DiscoveryCatalog.OpenAsync(Projection.DirectoryPath);
        var versions = Fixtures.Versions($"us|{buildKey}|{cdnKey}||70448|12.1.5.70448||geometry-authored");
        var attempt = await Fixtures.Attempt(versions, "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\nus|tpr/wow|us.cdn.blizzard.com|https://us.cdn.blizzard.com/\n");
        var generation = await Catalog.BeginRefreshAsync("wow", default); var page = await Catalog.CommitAsync("wow", generation, attempt, Time, default);
        var workspace = new WorkspaceRequest("wow", page.Builds.Single().Id, "us");
        Opening = await Catalog.ResolveOpeningAsync(workspace, default);
        Client = new(Http); Shared = new(new OfficialEncodedObjectProducer(Catalog, Projection.Objects, Client, Resources));
        Sessions = new(Catalog, Projection.Objects, Client, Shared, Resources);
        using (var configurations = new ConfigurationWorkspacePreparer(Catalog, Projection.Objects, Client, resources: Resources))
            await configurations.PrepareAsync(workspace, _ => Task.CompletedTask, default);
        // Simulate the pre-existing C1 table-source prerequisite through its real selected-table
        // pipeline BEFORE geometry B opens. The production geometry adapter never calls it.
        ContentReceipt content;
        using (var selected = new SelectedFileAcquirer(Catalog, Projection.Objects, Client, Shared, Resources))
        await using (var tableSource = await selected.AcquireAsync(workspace, uint.Parse(CreaturesPolicy.FileDataId), "enUS", _ => Task.CompletedTask, default))
            content = tableSource.Receipt;
        AssetSourceAssociation association; Dictionary<uint, AssetResolutionResult> memberships;
        await using (var handle = await Sessions.OpenAsync(workspace, "enUS", _ => Task.CompletedTask))
        {
            memberships = (await handle.ResolveAsync([ModelId, uint.Parse(CreaturesPolicy.FileDataId), 1108759, 1365368], _ => Task.CompletedTask)).Results.ToDictionary(r => r.FileDataId);
            association = memberships[ModelId].Association;
        }
        await Catalog.CommitWorkspaceAsync(Opening, association.Configurations, Time, default);
        var template = await Projection.Binding([new(1, "Authored Creature")]);
        var rowBinding = template.Rows.Binding with { SourceSha256 = content.Sha256 };
        await using var oldRows = new TableRowArtifacts(Projection.Objects); await using var sourceRows = new TableRowArtifacts(Projection.Objects, Resources, null);
        await using var writer = await sourceRows.CreateWriterAsync(rowBinding, default); await writer.HeaderAsync(template.Rows.Header, default);
        foreach (var row in (await oldRows.ReadPageAsync(template.Rows, 0, 50, null, default)).Rows) await writer.RowAsync(row, default);
        var rows = await writer.CompleteAsync(template.Rows.Completion with { ConsumedBytes = table.Decoded.Length }, default);
        SelectedFileAssociation TableAssociation(string fileId) => new(fileId, "enUS", SelectedFileAcquirer.VariantPolicy, association.EncodingSource, association.RootSource,
            memberships[uint.Parse(fileId)].Association.Memberships.Select(m => new SelectedRootMembership(N(m.BlockOrdinal), N(m.EntryOrdinal), m.Entry.ContentKey,
                N(m.Entry.LocaleFlags), N(m.Entry.ContentFlags), N(m.Entry.RawContentFlags.First), N(m.Entry.RawContentFlags.Second), N(m.Entry.RawContentFlags.Third),
                m.Entry.NameHash?.ToString(CultureInfo.InvariantCulture))).ToArray());
        var sourceAssociation = TableAssociation(CreaturesPolicy.FileDataId);
        var context = new CreaturesContext("wow", Opening.Build.Id, "us", "enUS");
        var binding = template with { Context = context, SourceReference = Opening.Reference, Configurations = association.Configurations, Rows = rows,
            Source = template.Source with { BuildIdentity = context.BuildIdentity, ArtifactId = rows.ContentKey, Source = content, Association = sourceAssociation } };
        await Catalog.RecordTableArtifactAsync(rows, default); await Catalog.CommitTableAdmissionAsync(Opening, CreaturesPolicy.Table, "enUS", content, sourceAssociation, rows, Time, default);
        var core = await Projection.Projection.BuildAsync(binding, sourceRows, _ => Task.CompletedTask, default);
        await Catalog.RecordCreaturesArtifactAsync(core, default); await Catalog.CommitCreaturesAdmissionAsync(Opening, core, Time, default);
        foreach (var role in new[] { CreatureDisplayTargetRole.Display, CreatureDisplayTargetRole.Model })
        {
            var definition = (await TablesCatalog.Provider.Value).Definitions[CreatureDisplayPolicy.Table(role)];
            var target = CreatureDisplayPolicyTests.Rows(definition.Document, role);
            var edge = CreatureDisplayPolicy.SelectField(definition.Document, Opening.Build.Version, target, role);
            var targetBinding = rowBinding with { DefinitionSha256 = definition.Sha256, ProviderRevision = BundledTableDefinitions.Revision,
                SchemaSelectionSha256 = TableRowArtifacts.SchemaSelectionKey(definition.Document, Opening.Build.Version) };
            await using var targetOwner = new TableRowArtifacts(Projection.Objects, Resources, null); await using var targetWriter = await targetOwner.CreateWriterAsync(targetBinding, default);
            await targetWriter.HeaderAsync(target.Header, default);
            var id = role == CreatureDisplayTargetRole.Display ? 123u : 33u;
            var values = target.Header.Columns.Select(c => c.Kind == "string" ? "" : "0").ToArray();
            for (var i = 0; i < values.Length; i++) if (target.Header.Columns[i].IsId) values[i] = N(id);
            values[edge] = role == CreatureDisplayTargetRole.Display ? "33" : N(ModelId);
            if (role == CreatureDisplayTargetRole.Display && textured is not null)
            {
                int Column(string name) => target.Header.Columns.Select((c, i) => (c, i)).Single(x => x.c.Name == name).i;
                values[Column("ExtendedDisplayInfoID")] = N(textured.Extended); values[Column("TextureVariationFileDataID[0]")] = N(textured.Variation);
            }
            await targetWriter.RowAsync(new(id, false, null, values), default);
            var artifact = await targetWriter.CompleteAsync(target.Completion with { ConsumedBytes = table.Decoded.Length }, default);
            var targetAssociation = TableAssociation(role == CreatureDisplayTargetRole.Display ? "1108759" : "1365368");
            await Catalog.RecordTableArtifactAsync(artifact, default);
            await Catalog.CommitTableAdmissionAsync(Opening, CreatureDisplayPolicy.Table(role), "enUS", content, targetAssociation, artifact, Time, default);
        }
        Tables = new(new NoTables()); Creatures = new(Catalog, Projection.Objects, Tables, Resources);
        Displays = new(new CreatureDisplaySources(Catalog, Projection.Objects, Creatures, Tables, Resources));
        Request = new(context, core.ContentKey, "1", 0);
        Selection = (await Displays.AuthorizeGeometryAsync(Request, default)).Authorization ?? throw new InvalidOperationException("Authored exact C2 chain failed.");
        Fence = new(this); Services = new(Catalog, Projection.Objects, Sessions, Shared, Resources);
    }
    internal async Task<GeometryPreparedSources> PrepareAsync(Func<CreatureGeometryProgress, Task>? progress = null, CancellationToken token = default)
    {
        if (Prepared is not null) { await Prepared.DisposeAsync(); Prepared = null; capture!.Dispose(); Producer!.Dispose(); }
        Producer = Payload.ReserveProducer(); capture = Producer.Reserve(16 << 20);
        try { return Prepared = await GeometrySourceClosure.PrepareAsync(Services, Fence, Producer, progress ?? (_ => Task.CompletedTask), token); }
        catch { capture.Dispose(); capture = null; Producer.Dispose(); Producer = null; throw; }
    }
    internal sealed class FixtureFence(CreatureGeometrySourcesFixture fixture) : IGeometrySelectionFence
    {
        internal int Checks; internal Func<int, CancellationToken, Task>? Before;
        public GeometryDisplayAuthorization Original => fixture.Selection;
        public async Task RevalidateAsync(CancellationToken token)
        {
            var n = Interlocked.Increment(ref Checks); if (Before is not null) await Before(n, token);
            var fresh = (await fixture.Displays.AuthorizeGeometryAsync(fixture.Request, token)).Authorization;
            if (fresh is null || JsonSerializer.Serialize(fresh, CreaturesPolicy.Json) != JsonSerializer.Serialize(Original, CreaturesPolicy.Json))
                throw new DiscoveryException("CREATURE_GEOMETRY_STALE", "Authored selection changed.");
        }
    }
    internal sealed class Handler : HttpMessageHandler
    {
        internal readonly Dictionary<string, byte[]> Responses = new(StringComparer.Ordinal);
        internal readonly List<string> Requests = []; internal bool Offline;
        internal Func<HttpRequestMessage, CancellationToken, Task>? Before;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!.AbsoluteUri; Requests.Add(uri); if (Before is not null) await Before(request, token);
            if (Offline) throw new HttpRequestException("Authored offline");
            if (!Responses.TryGetValue(uri, out var bytes)) return new(HttpStatusCode.NotFound) { RequestMessage = request };
            // Large objects are fetched in ranges; answer each with its exact slice.
            if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from } range)
            {
                var to = Math.Min(range.To ?? bytes.Length - 1, bytes.Length - 1);
                var content = new ByteArrayContent(bytes, (int)from, (int)(to - from + 1));
                content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, bytes.Length);
                return new(HttpStatusCode.PartialContent) { RequestMessage = request, Content = content };
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        }
    }
    private sealed class NoTables : ITableDataStore
    {
        public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => throw new InvalidOperationException("No prerequisite needed.");
        public Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token) => throw new InvalidOperationException("No acquisition through Tables.");
        public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("No per-row Tables call.");
    }
    private async Task Publish(byte[] bytes)
    { string path; await using (var file = await Projection.Objects.CreateStagingFileAsync(default)) { path = file.Name; await file.WriteAsync(bytes); } await Projection.Objects.PublishFileAsync(path, bytes.Length, Resources, null, default); }
    private sealed record Blob(string Role, string ContentKey, string EncodingKey, byte[] Decoded, byte[] Encoded);
    private static Blob Object(string role, byte[] decoded)
    { byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded]; return new(role, Hash(decoded), Hash(encoded), decoded, encoded); }
    private static byte[] ModelBytes(bool externalSkeleton)
    {
        var body = new byte[304 + 3 * 48]; "MD20"u8.CopyTo(body); Put(body, 4, 274); Put(body, 60, 3); Put(body, 64, 304); Put(body, 68, 1);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(304 + 48), 1); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(304 + 96 + 4), 2);
        for (var i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(304 + i * 48 + 28), 1);
        var result = new byte[body.Length + 20]; "MD21"u8.CopyTo(result); Put(result, 4, (uint)body.Length); body.CopyTo(result, 8);
        "SFID"u8.CopyTo(result.AsSpan(body.Length + 8)); Put(result, body.Length + 12, 4); Put(result, body.Length + 16, SkinId);
        if (externalSkeleton) { var expanded = new byte[result.Length + 12]; result.CopyTo(expanded, 0); "SKID"u8.CopyTo(expanded.AsSpan(result.Length)); Put(expanded, result.Length + 4, 4); Put(expanded, result.Length + 8, 900); return expanded; }
        return result;
    }
    private static byte[] SkinBytes(bool empty)
    {
        // The batch bytes are deliberately opaque. A strict material path has no valid texture
        // closure; the geometry-only API must still preserve every valid declared triangle.
        var result = new byte[56 + 6 + 6 + 12 + 48 + 24]; "SKIN"u8.CopyTo(result);
        Put(result, 4, 3); Put(result, 8, 56); Put(result, 12, 3); Put(result, 16, 62); Put(result, 20, 3); Put(result, 24, 68);
        Put(result, 28, 1); Put(result, 32, 80); Put(result, 36, 1); Put(result, 40, 128);
        for (var i = 0; i < 3; i++) { W(result, 56 + i * 2, (ushort)i); W(result, 62 + i * 2, (ushort)i); }
        W(result, 86, 3); W(result, 90, empty ? (ushort)0 : (ushort)3); result[128] = 255;
        return result;
    }
    private static byte[] Root(Blob model, Blob skin, Blob table, bool ambiguousModel, bool missingModel, Blob? texture)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        var entries = new[] { (ModelId, model), (SkinId, skin), (uint.Parse(CreaturesPolicy.FileDataId), table), (1108759u, table), (1365368u, table) }
            .Concat(texture is null ? [] : [(TextureId, texture)]).Where(x => !missingModel || x.Item1 != ModelId).OrderBy(x => x.Item1).ToArray();
        w.Write("TSFM"u8); w.Write(24u); w.Write(2u); w.Write((uint)(entries.Length + (ambiguousModel ? 1 : 0))); w.Write((uint)(entries.Length + (ambiguousModel ? 1 : 0))); w.Write(0u);
        w.Write((uint)entries.Length); w.Write(2u); w.Write(0u); w.Write(0u); w.Write((byte)0);
        uint previous = 0; foreach (var (id, _) in entries) { w.Write(id - previous); previous = id + 1; }
        foreach (var (_, blob) in entries) w.Write(Convert.FromHexString(blob.ContentKey));
        foreach (var (id, _) in entries) w.Write((ulong)id);
        if (ambiguousModel) { w.Write(1u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write((byte)0); w.Write(ModelId); w.Write(Convert.FromHexString(table.ContentKey)); w.Write((ulong)ModelId); }
        return stream.ToArray();
    }
    private static byte[] EncodingManifest(Blob[] blobs, bool oversizedModel)
    {
        var cp = new byte[1024]; var ep = new byte[1024]; var contents = blobs.OrderBy(b => b.ContentKey, StringComparer.Ordinal).ToArray(); var encodings = blobs.OrderBy(b => b.EncodingKey, StringComparer.Ordinal).ToArray();
        using (var p = new MemoryStream(cp)) foreach (var b in contents) { p.WriteByte(1); Big(p, b.Decoded.Length, 5); p.Write(Convert.FromHexString(b.ContentKey)); p.Write(Convert.FromHexString(b.EncodingKey)); }
        using (var p = new MemoryStream(ep)) foreach (var b in encodings) { p.Write(Convert.FromHexString(b.EncodingKey)); Big(p, 0, 4); Big(p, oversizedModel && b.Role == "model" ? (4 << 20) + 1 : b.Encoded.Length, 5); }
        using var output = new MemoryStream(); output.Write("EN"u8); output.Write([1, 16, 16]); Big(output, 1, 2); Big(output, 1, 2); Big(output, 1, 4); Big(output, 1, 4); output.WriteByte(0);
        byte[] spec = "b:*=n\0"u8.ToArray(); Big(output, spec.Length, 4); output.Write(spec);
        output.Write(Convert.FromHexString(contents[0].ContentKey)); output.Write(MD5.HashData(cp)); output.Write(cp);
        output.Write(Convert.FromHexString(encodings[0].EncodingKey)); output.Write(MD5.HashData(ep)); output.Write(ep); return output.ToArray();
    }
    private static byte[] Encrypted(int decoded)
    {
        byte[] payload = [(byte)'E', 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0, 0, 0, 0, (byte)'S', 9];
        var encoded = new byte[36 + payload.Length]; "BLTE"u8.CopyTo(encoded); BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(4), 36); encoded[8] = 15; encoded[11] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(12), (uint)payload.Length); BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(16), (uint)decoded); MD5.HashData(payload).CopyTo(encoded, 20); payload.CopyTo(encoded, 36); return encoded;
    }
    private static void Put(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void W(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static void Big(Stream s, long value, int size) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)value); s.Write(b[(8 - size)..]); }
    private static string Hash(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(MD5.HashData(b));
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync()
    {
        if (Prepared is not null) await Prepared.DisposeAsync(); capture?.Dispose(); Producer?.Dispose();
        if (Displays is not null) await Displays.DisposeAsync(); if (Creatures is not null) await Creatures.DisposeAsync(); if (Tables is not null) await Tables.DisposeAsync();
        if (Sessions is not null) await Sessions.DisposeAsync(); if (Shared is not null) await Shared.DisposeAsync(); Client?.Dispose();
        if (Catalog is not null) await Catalog.DisposeAsync(); await Projection.DisposeAsync();
    }
}
