using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
namespace VibeMiner.Infrastructure;

/// <summary>One bounded metadata-only preparation; encoded payload acquisition is never initialized here.</summary>
public sealed class InventoryWorkspacePreparer(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client,
    AcquisitionResources? resources = null)
    : IWorkspacePreparer, IDisposable
{
    public const string Profile = "home-inventory-v1";
    private readonly ConfigurationWorkspacePreparer configurations = new(catalog, objects, client, resources: resources);
    private readonly MetadataAcquirer acquisition = new(client, objects, resources: resources);
    private readonly SemaphoreSlim parser = new(1, 1);
    private readonly SemaphoreSlim operation = new(1, 1);
    public Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token) =>
        PrepareWithProgressAsync(request, value => progress(value.Stage), token);
    public async Task<WorkspaceSnapshot> PrepareWithProgressAsync(WorkspaceRequest request, Func<WorkspacePreparationProgress, Task> progress, CancellationToken token)
    {
        if (!await operation.WaitAsync(0, token)) throw new DiscoveryException("OPEN_BUSY", "Another preparation is active.", true);
        try { return await PrepareCoreAsync(request, progress, token); }
        finally { operation.Release(); }
    }
    private async Task<WorkspaceSnapshot> PrepareCoreAsync(WorkspaceRequest request, Func<WorkspacePreparationProgress, Task> report, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(20)); var ct = lifetime.Token;
        long? staging = null;
        var plan = new PreparationProgressPlan(report, ct);
        Task progress(string stage) => plan.ReportAsync(stage);
        try
        {
            await plan.BeginAsync(PreparationStage.Configurations, "Checking build configurations");
            var inputs = await configurations.PrepareInputsAsync(request, progress, ct);
            var declarations = MetadataDeclarations.Parse(inputs.BuildConfiguration, ct);
            var previous = await catalog.FindInventoryAsync(inputs.Context, ct);
            var metadata = new List<MetadataReceipt>(5); long encodedWork = 0, decodedWork = 0;
            long activeGeneration = previous?.Generation ?? 0;
            if (previous is not null)
            {
                ValidateSources(declarations, previous.Metadata);
                plan.ConfigureCached(previous.Metadata.Select(receipt => receipt.Role));
                await progress("Verified build and CDN configurations");
                await plan.BeginAsync(PreparationStage.CachedIndex, "Verifying saved catalog index");
                var expectedDigest = await catalog.InventoryDigestAsync(previous.Generation, ct);
                if (expectedDigest is null || expectedDigest != await catalog.ComputeInventoryDigestAsync(previous.Generation, ct, progress))
                    throw new DiscoveryException("LOCAL_INVENTORY_CORRUPT", "The saved catalog index failed integrity verification. No automatic rebuild was attempted.");
                foreach (var role in new[] { "encoding", "root", "install", "download", "size" })
                {
                    var receipt = previous.Metadata.SingleOrDefault(receipt => receipt.Role == role);
                    if (receipt is null) continue;
                    await plan.BeginAsync(PreparationProgressPlan.Revalidation(role), $"Revalidating saved {role} metadata");
                    ct.ThrowIfCancellationRequested();
                    var saved = await catalog.FindMetadataAsync(receipt.EncodingKey, ct);
                    if (saved is null || saved.Sha256 != receipt.Sha256 || saved.ContentKey != receipt.ContentKey || saved.EncodedBytes != receipt.EncodedBytes || saved.DecodedBytes != receipt.DecodedBytes || receipt.ParserRevision != receipt.Role + "-v1")
                        throw new DiscoveryException("CATALOG_INTEGRITY", "A saved inventory source proof is incomplete or unsupported.");
                    var expectation = new MetadataExpectation(receipt.Role, receipt.ContentKey, receipt.EncodingKey, Number(receipt.EncodedBytes), Number(receipt.DecodedBytes));
                    Reserve(expectation);
                    await using var source = await acquisition.AcquireAsync(inputs.Route, expectation, progress, ct, receipt);
                    metadata.Add(source.Receipt with { ParserRevision = receipt.ParserRevision });
                }
                return await Finish(previous.Generation);
            }
            plan.ConfigureFresh(declarations.Download is not null, declarations.Size is not null);
            await progress("Verified build and CDN configurations");
            var resumed = await catalog.FindResumableInventoryAsync(inputs.Context, ct, progress);
            long generation;
            if (resumed is null)
            {
                await plan.BeginAsync(PreparationStage.Cleanup, "Cleaning up unfinished catalog data");
                await catalog.CleanupStagingInventoriesAsync(ct, progress);
                generation = await catalog.StartInventoryAsync(inputs.Context, ct);
            }
            else
            {
                generation = resumed.Generation;
                await progress("Restoring saved preparation checkpoints");
                await catalog.ResetIncompleteInventoryAsync(generation, ct, progress);
            }
            staging = generation; activeGeneration = generation;
            await catalog.EnsureInventoryReferenceIndexAsync(ct, progress);
            var checkpoints = resumed?.Checkpoints.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, InventoryCheckpoint>(StringComparer.Ordinal);
            MetadataReceipt? activeEncodingReceipt = null;
            var consumer = new InventoryConsumer(catalog, generation, progress, async table =>
            {
                if (table == EncodingTable.Encoding && !checkpoints.ContainsKey("encoding-content"))
                    await Checkpoint("encoding-content", activeEncodingReceipt);
                await plan.BeginAsync(table == EncodingTable.Content ? PreparationStage.EncodingContentIndex : PreparationStage.EncodingObjectIndex,
                    table == EncodingTable.Content ? checkpoints.ContainsKey("encoding-content") ? "Checking saved encoding content index" : "Indexing encoding content" : "Indexing encoding objects");
            }, resumed?.LastOriginId ?? 0, resumed?.FamilyCounts, checkpoints.ContainsKey("encoding-content"));
            await ReadSource(declarations.Encoding, "encoding", async (stream, cancellation) =>
                { await EncodingInventoryReader.ReadAsync(stream, new(), consumer, cancellation); await consumer.CompleteEncodingAsync(cancellation); });
            await plan.BeginAsync(PreparationStage.RootAcquisition, "Resolving ROOT metadata source");
            var root = await Resolve(new("root", declarations.RootContentKey, null, null, null));
            await ReadSource(root, "root", async (stream, cancellation) =>
                { await RootInventoryReader.ReadAsync(stream, new(), consumer, cancellation); await consumer.FlushAsync(cancellation); });
            await plan.BeginAsync(PreparationStage.InstallAcquisition, "Resolving installation metadata source");
            var install = await Resolve(declarations.Install);
            await ReadSource(install, "install", async (stream, cancellation) =>
                { await InstallInventoryReader.ReadAsync(stream, new(), consumer, cancellation); await consumer.FlushAsync(cancellation); });
            if (declarations.Download is not null)
            {
                await plan.BeginAsync(PreparationStage.DownloadAcquisition, "Resolving download metadata source");
                var download = await Resolve(declarations.Download);
                await ReadSource(download, "download", async (stream, cancellation) =>
                    { await DownloadInventoryReader.ReadAsync(stream, new(), consumer, cancellation); await consumer.FlushAsync(cancellation); });
                await ResolveEncoded("download", PreparationStage.DownloadResolution, PreparationStage.DownloadSave);
            }
            if (declarations.Size is not null)
            {
                await plan.BeginAsync(PreparationStage.SizeAcquisition, "Resolving size metadata source");
                var size = await Resolve(declarations.Size);
                await ReadSource(size, "size", async (stream, cancellation) =>
                    { await DownloadSizeInventoryReader.ReadAsync(stream, new(), consumer, cancellation); await consumer.FlushAsync(cancellation); });
                await ResolveEncoded("size", PreparationStage.SizeResolution, PreparationStage.SizeSave);
            }
            IReadOnlyList<string>? verifiedForSelection = null;
            foreach (var family in new[] { "root", "install" })
            {
                await plan.BeginAsync(family == "root" ? PreparationStage.RootResolution : PreparationStage.InstallResolution,
                    $"Resolving {family} inventory");
                if (checkpoints.ContainsKey(family + "-resolved"))
                {
                    await progress($"Using saved {family} inventory resolution");
                    continue;
                }
                if (family == "root")
                {
                    verifiedForSelection = await Assess(await catalog.AvailableCandidatesAsync(generation, ct));
                    await catalog.SetAvailableAsync(generation, verifiedForSelection, ct);
                }
                var total = consumer.Count(family);
                var position = await catalog.ResolutionPositionAsync(generation, family, ct);
                long after = position.After, completed = position.Completed; var batches = 0;
                var lastProgress=System.Diagnostics.Stopwatch.GetTimestamp();
                await progress(InventoryProgress.Format($"Resolving {family} inventory", completed, total, "entries"));
                while (true)
                {
                    var batch = await catalog.ResolveOriginBatchAsync(generation, family, after, ct);
                    if (batch.Count == 0) break;
                    after = batch.After; completed += batch.Count;
                    if (++batches % 16 == 0 || completed == total || System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress).TotalSeconds>=1)
                    {
                        await progress(InventoryProgress.Format($"Resolving {family} inventory", completed, total, "entries"));
                        lastProgress=System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
                await Checkpoint(family + "-resolved");
            }
            await plan.BeginAsync(PreparationStage.CatalogDigest, "Verifying complete catalog index");
            var digest = await catalog.ComputeInventoryDigestAsync(generation, ct, progress);
            await catalog.StoreInventoryDigestAsync(generation, digest, ct);
            var result = await Finish(generation); staging = null; return result;

            async Task ResolveEncoded(string family, PreparationStage resolving, PreparationStage saving)
            {
                if (checkpoints.ContainsKey(family + "-resolved"))
                {
                    await plan.BeginAsync(saving, $"Using saved {family} inventory resolution");
                    return;
                }
                await plan.BeginAsync(resolving, $"Resolving {family} inventory");
                await catalog.ResolveOrderedEncodedAsync(generation, family, consumer.Count(family), progress, ct,
                    () => plan.Select(saving));
                await Checkpoint(family + "-resolved");
            }

            async Task Checkpoint(string kind, MetadataReceipt? receipt = null) =>
                checkpoints[kind] = await catalog.SaveInventoryCheckpointAsync(generation, kind, receipt, ct, progress);

            async Task<MetadataExpectation> Resolve(MetadataDeclaration declaration)
            {
                if (declaration.DirectExpectation is { } direct) return direct;
                var (decoded, candidates) = await catalog.ResolveContentAsync(generation, declaration.ContentKey, ct);
                var selected = declaration.EncodingKey is null ? candidates[0] : candidates.SingleOrDefault(c => c.Key == declaration.EncodingKey)
                    ?? throw new DiscoveryException("METADATA_UNRESOLVED", "A declared metadata encoding is absent from EN.");
                if (declaration.DecodedBytes is not null && declaration.DecodedBytes != decoded || declaration.EncodedBytes is not null && declaration.EncodedBytes != selected.Bytes)
                    throw new DiscoveryException("METADATA_SIZE_CONFLICT", "Metadata declarations disagree with the verified encoding table.");
                return new(declaration.Role, declaration.ContentKey, selected.Key, selected.Bytes, decoded);
            }
            async Task ReadSource(MetadataExpectation expectation, string role, Func<Stream, CancellationToken, Task> parse)
            {
                await plan.BeginAsync(PreparationProgressPlan.Acquisition(role), $"Preparing {role} metadata");
                Reserve(expectation);
                var retained = await catalog.FindMetadataAsync(expectation.EncodingKey, ct);
                await using var source = await acquisition.AcquireAsync(inputs.Route, expectation, progress, ct, retained);
                // Source bytes are fully verified already; their receipt survives interrupted parsing.
                // This acquisition revision does not claim that the format parser reached EOF.
                await catalog.RecordMetadataAsync(source.Receipt, ct);
                if (role == "encoding" && checkpoints.TryGetValue("encoding-content", out var savedContent) &&
                    (savedContent.Source is not { } contentProof || contentProof.Sha256 != source.Receipt.Sha256 ||
                     contentProof.ContentKey != expectation.ContentKey || contentProof.EncodingKey != expectation.EncodingKey ||
                     Number(contentProof.EncodedBytes) != expectation.EncodedBytes || Number(contentProof.DecodedBytes) != expectation.DecodedBytes))
                    throw new DiscoveryException("CHECKPOINT_INTEGRITY", "Saved encoding content differs from its verified metadata source.");
                if (checkpoints.TryGetValue(role, out var completed))
                {
                    if (completed.Source is not { } proof || proof.Sha256 != source.Receipt.Sha256 ||
                        proof.ContentKey != expectation.ContentKey || proof.EncodingKey != expectation.EncodingKey ||
                        Number(proof.EncodedBytes) != expectation.EncodedBytes || Number(proof.DecodedBytes) != expectation.DecodedBytes ||
                        proof.ParserRevision != role + "-v1")
                        throw new DiscoveryException("CHECKPOINT_INTEGRITY", "Saved preparation differs from its verified metadata source.");
                    await plan.BeginAsync(role == "encoding" ? PreparationStage.EncodingObjectIndex : PreparationProgressPlan.Index(role),
                        $"Using saved {role} index");
                    metadata.Add(source.Receipt with { ParserRevision = role + "-v1" });
                    return;
                }
                if (role == "encoding") activeEncodingReceipt = source.Receipt;
                await using var decoded = source.OpenDecoded();
                await plan.BeginAsync(PreparationProgressPlan.Index(role), role == "encoding" && checkpoints.ContainsKey("encoding-content")
                    ? "Checking saved encoding content index" : $"Indexing {role} metadata");
                await parser.WaitAsync(ct);
                try { await parse(decoded, ct); }
                finally { parser.Release(); }
                var receipt = source.Receipt with { ParserRevision = role + "-v1" };
                await catalog.RecordMetadataAsync(receipt, ct);
                await Checkpoint(role, receipt);
                metadata.Add(receipt);
            }
            async Task<WorkspaceSnapshot> Finish(long id)
            {
                await plan.BeginAsync(PreparationStage.SharedAssessment, "Checking verified shared objects");
                var retained = await Assess(await catalog.RetainedCandidatesAsync(id, ct));
                await plan.BeginAsync(PreparationStage.Seal, "Sealing file catalog and download sizes");
                return await catalog.SealInventoryAsync(id, inputs.Context, inputs.Configurations, metadata, retained, ct);
            }
            async Task<IReadOnlyList<string>> Assess(IReadOnlyList<(string Key, MetadataReceipt Receipt)> candidates)
            {
                var retained = new List<string>();
                foreach (var (key, receipt) in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var expected = await catalog.CandidateLengthAsync(activeGeneration, key, ct);
                    if (receipt.EncodingKey != key || expected != Number(receipt.EncodedBytes)) continue;
                    try
                    {
                        var decoded = Number(receipt.DecodedBytes); decodedWork = checked(decodedWork + decoded);
                        if (decodedWork > 2L * 1024 * 1024 * 1024) throw new DiscoveryException("METADATA_BUDGET", "Shared-object verification exceeds the decoded work budget.");
                        await using (var verified = resources is null
                            ? await objects.OpenVerifiedAsync(receipt.Sha256, Number(receipt.EncodedBytes), ct)
                            : await objects.OpenVerifiedAsync(receipt.Sha256, Number(receipt.EncodedBytes), resources, null, ct))
                        using (resources is null ? null : await resources.EnterCpuAsync(null, ct))
                            await BlteDecoder.DecodeAsync(verified, Stream.Null, key, receipt.ContentKey, Number(receipt.EncodedBytes), decoded, ct);
                        await catalog.ValidateCandidateContentAsync(activeGeneration, receipt, ct);
                        retained.Add(key);
                    }
                    catch (DiscoveryException e) when (e.Code.StartsWith("LOCAL_OBJECT_", StringComparison.Ordinal) || e.Code.StartsWith("BLTE_", StringComparison.Ordinal))
                    { /* Unverified payload bytes remain additional demand; required metadata failures were handled above. */ }
                }
                return retained;
            }
            void Reserve(MetadataExpectation expectation)
            {
                encodedWork = checked(encodedWork + expectation.EncodedBytes); decodedWork = checked(decodedWork + expectation.DecodedBytes);
                if (encodedWork > 512L * 1024 * 1024 || decodedWork > 2L * 1024 * 1024 * 1024)
                    throw new DiscoveryException("METADATA_BUDGET", "Preparation exceeds the aggregate metadata byte budget.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new DiscoveryException("OPEN_TIMEOUT", "Preparation exceeded its bounded time budget.", true); }
        catch (IOException) { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Metadata storage could not complete preparation.", true); }
        catch (UnauthorizedAccessException) { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Metadata storage could not be accessed.", true); }
        finally { if (staging is { } abandoned) await catalog.AbandonInventoryAsync(abandoned); }
    }
    private static long Number(string value) => long.Parse(value, CultureInfo.InvariantCulture);
    private static void ValidateSources(MetadataDeclarationSet declarations, IReadOnlyList<MetadataReceipt> receipts)
    {
        var expected = new List<(string Role, string CKey, MetadataExpectation? Direct)> { ("encoding", declarations.Encoding.ContentKey, declarations.Encoding), ("root", declarations.RootContentKey, null), ("install", declarations.Install.ContentKey, declarations.Install.DirectExpectation) };
        if (declarations.Download is { } dl) expected.Add(("download", dl.ContentKey, dl.DirectExpectation));
        if (declarations.Size is { } ds) expected.Add(("size", ds.ContentKey, ds.DirectExpectation));
        if (receipts.Count != expected.Count || receipts.Select(r => r.Role).Distinct().Count() != receipts.Count)
            throw new DiscoveryException("CATALOG_INTEGRITY", "Saved inventory roles do not match the authenticated declarations.");
        foreach (var (role, ckey, direct) in expected)
        {
            var receipt = receipts.SingleOrDefault(r => r.Role == role);
            if (receipt is null || receipt.ContentKey != ckey || direct is not null && (receipt.EncodingKey != direct.EncodingKey || Number(receipt.EncodedBytes) != direct.EncodedBytes || Number(receipt.DecodedBytes) != direct.DecodedBytes))
                throw new DiscoveryException("CATALOG_INTEGRITY", "Saved inventory sources differ from their exact declarations.");
        }
    }
    public void Dispose() { configurations.Dispose(); parser.Dispose(); operation.Dispose(); }
}

internal sealed class InventoryConsumer(DiscoveryCatalog catalog, long generation, Func<string, Task> progress,
    Func<EncodingTable, Task>? encodingTableStarted = null, long initialId = 0,
    IReadOnlyDictionary<string, long>? initialCounts = null, bool skipEncodingContent = false)
    : IEncodingConsumer, IRootConsumer, IInstallConsumer, IDownloadConsumer, IDownloadSizeConsumer
{
    private readonly List<EncodingContentRow> contents = [];
    private readonly List<EncodingObjectRow> encodings = [];
    private readonly List<InventoryOrigin> origins = [];
    private const int BatchRows = 1024;
    private const int BatchBytes = 1 << 20;
    private long id = initialId, work; private int references, originBytes;
    private EncodingHeader? encodingHeader;
    private EncodingTable? currentEncodingTable;
    private readonly Dictionary<string, long> counts = initialCounts?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? [];
    private readonly Dictionary<string, long> totals = [];
    public long Count(string family) => counts.GetValueOrDefault(family);
    public async ValueTask HeaderAsync(EncodingHeader header, CancellationToken token)
    { encodingHeader = header; await BeginEncodingTableAsync(EncodingTable.Content, token); }
    public ValueTask HeaderAsync(RootHeader header, CancellationToken token) { totals["root"] = header.MembershipCount; return ValueTask.CompletedTask; }
    public ValueTask HeaderAsync(InstallHeader header, CancellationToken token) { totals["install"] = header.EntryCount; return ValueTask.CompletedTask; }
    public ValueTask HeaderAsync(DownloadHeader header, CancellationToken token) { totals["download"] = header.EntryCount; return ValueTask.CompletedTask; }
    public ValueTask HeaderAsync(DownloadSizeHeader header, CancellationToken token) { totals["size"] = header.EntryCount; return ValueTask.CompletedTask; }
    private const long MaxOrigins = 50_000_000;
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    public async ValueTask ContentAsync(int pageOrdinal, long ordinal, EncodingContentEntry entry, CancellationToken token)
    {
        await BeginEncodingTableAsync(EncodingTable.Content, token);
        if (skipEncodingContent) return;
        contents.Add(new(entry.ContentKey, entry.DecodedBytes, entry.EncodingKeys)); references += entry.EncodingKeys.Count;
        if (contents.Count >= BatchRows || references >= 2048) await FlushAsync(token);
    }
    public async ValueTask EncodingAsync(int pageOrdinal, long ordinal, EncodingEncodedEntry entry, CancellationToken token)
    { await BeginEncodingTableAsync(EncodingTable.Encoding, token); encodings.Add(new(entry.EncodingKey, entry.EncodedBytes)); if (encodings.Count >= BatchRows) await FlushAsync(token); }
    public async ValueTask PageCompletedAsync(EncodingTable table, int pageOrdinal, CancellationToken token)
    {
        await BeginEncodingTableAsync(table, token);
        var total = table == EncodingTable.Content ? encodingHeader!.ContentPageCount : encodingHeader!.EncodingPageCount;
        if (pageOrdinal % 128 == 0 || pageOrdinal + 1 == total)
            await progress(InventoryProgress.Format(table == EncodingTable.Content ? skipEncodingContent ? "Checking saved encoding content index" : "Indexing encoding content" : "Indexing encoding objects", pageOrdinal + 1, total, "pages"));
    }
    private async Task BeginEncodingTableAsync(EncodingTable table, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (currentEncodingTable == table) return;
        if (currentEncodingTable == EncodingTable.Encoding && table == EncodingTable.Content)
            throw new InvalidOperationException("Encoding content cannot follow the encoded object table.");
        await FlushAsync(token);
        if (encodingTableStarted is not null) await encodingTableStarted(table);
        token.ThrowIfCancellationRequested();
        currentEncodingTable = table;
    }
    internal async Task CompleteEncodingAsync(CancellationToken token)
    {
        // Zero-page tables are actual completed phases too, even without a row/page callback.
        await BeginEncodingTableAsync(EncodingTable.Encoding, token);
        await FlushAsync(token);
    }
    public ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken token) => Add(new(++id, "root", null, N(entry.FileDataId), entry.ContentKey, null, 0, null, null,
        N(entry.LocaleFlags), N(entry.ContentFlags), entry.NameHash?.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(new { blockOrdinal, entryOrdinal, entry.RawContentFlags })), token);
    public ValueTask EntryAsync(long ordinal, InstallEntry entry, CancellationToken token) => Add(new(++id, "install", entry.Name, null, entry.ContentKey, null, 0, null, entry.DecodedBytes, null, null, null, N(ordinal)), token);
    public ValueTask EntryAsync(long ordinal, DownloadEntry entry, CancellationToken token) => Add(new(++id, "download", null, null, null, entry.EncodingKey, 16, entry.EncodedBytes, null, null, null, null,
        JsonSerializer.Serialize(new { ordinal, entry.PriorityRaw, entry.Checksum, Flags = Convert.ToHexStringLower(entry.Flags.Span) })), token);
    public ValueTask EntryAsync(long ordinal, DownloadSizeEntry entry, CancellationToken token) => Add(new(++id, "size", null, null, null, entry.EncodedKey.Hex, entry.EncodedKey.ByteLength, entry.EncodedBytes, null, null, null, null, N(ordinal)), token);
    public ValueTask TagAsync(int ordinal, InstallTag tag, CancellationToken token) => Tag("install", ordinal, tag.Name, tag.RawType, tag.Membership, token);
    public ValueTask TagAsync(int ordinal, DownloadTag tag, CancellationToken token) => Tag("download", ordinal, tag.Name, tag.RawType, tag.Membership, token);
    public ValueTask TagAsync(int ordinal, DownloadSizeTag tag, CancellationToken token) => Tag("size", ordinal, tag.Name, tag.RawType, tag.Membership, token);
    private async ValueTask Tag(string family, int ordinal, string name, ushort type, ReadOnlyMemory<byte> bits, CancellationToken token)
    { await catalog.AddTagAsync(generation, family, ordinal, name, type, bits, token); }
    private async ValueTask Add(InventoryOrigin origin, CancellationToken token)
    {
        if (id > MaxOrigins) throw new DiscoveryException("INVENTORY_LIMIT", "The complete inventory exceeds its membership bound.");
        counts[origin.Family] = Count(origin.Family) + 1;
        var bytes=512+2*(origin.Name?.Length ?? 0)+2*origin.Raw.Length;
        if (origins.Count>0 && originBytes+bytes>BatchBytes) await FlushAsync(token);
        origins.Add(origin);originBytes+=bytes;
        if (origins.Count>=BatchRows || originBytes>=BatchBytes) await FlushAsync(token);
    }
    public async Task FlushAsync(CancellationToken token)
    {
        if (contents.Count > 0 || encodings.Count > 0)
        { await catalog.AddEncodingAsync(generation, contents, encodings, token); work += contents.Count + encodings.Count; contents.Clear(); encodings.Clear(); references = 0; }
        if (origins.Count > 0)
        {
            var family = origins[^1].Family;
            await catalog.AddOriginsAsync(generation, origins, token); work += origins.Count; origins.Clear();originBytes=0;
            if (work % 16384 < BatchRows || Count(family) == totals.GetValueOrDefault(family))
                await progress(InventoryProgress.Format($"Indexing {family} metadata", Count(family), totals[family], "entries"));
        }
    }
}
