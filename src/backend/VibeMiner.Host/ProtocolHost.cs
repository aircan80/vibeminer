using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

public sealed class ProtocolHost(DiscoveryService service, WorkspaceService? workspace = null, InventoryService? inventory = null,
    DownloadService? downloads = null, TablesService? tables = null, TableDataService? tableData = null,
    RetentionService? retention = null, ItemsService? items = null,
    ItemIconsService? itemIcons = null, ItemIconLeases? iconLeases = null,
    ItemClassificationsService? itemClassifications = null, CreaturesService? creatures = null,
    CreatureDisplayMetadataService? creatureDisplays = null, ItemEffectsService? itemEffects = null,
    CreatureGeometryService? creatureGeometry = null, CreatureGeometryLeases? geometryLeases = null,
    SettingsService? settings = null,
    DownloadSummaryService? downloadSummaries = null, TableReferenceTextService? referenceText = null, TableFollowService? follow = null,
    PreparedDataService? preparedData = null, ICommunityListfileControl? listfile = null)
{
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxClassificationFrameBytes = 512 * 1024;
    public const int MaxCreaturesFrameBytes = 512 * 1024;
    public const int MaxItemEffectsFrameBytes = 512 * 1024;
    public const int MaxCreatureGeometryFrameBytes = 64 * 1024;
    private enum FrameKind { General, Classifications, Creatures, ItemEffects, CreatureGeometry }
    private DiscoveryException? geometryCleanupFailure;
    private DiscoveryException? effectsCleanupFailure;
    private readonly ConcurrentDictionary<string, ActiveRequest> active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outputLock = new(1, 1);
    private readonly SemaphoreSlim classificationPublication = new(2, 2);
    private readonly SemaphoreSlim classificationEncoding = new(1, 1);
    private readonly SemaphoreSlim creaturesPreparation = new(2, 2);
    private readonly SemaphoreSlim creaturesPublication = new(4, 4);
    private readonly SemaphoreSlim creaturesEncoding = new(1, 1);
    private readonly SemaphoreSlim effectsEncoding = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class ActiveRequest : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private bool disposed;
        public void Cancel() { lock (gate) { if (!disposed) Cancellation.Cancel(); } }
        public void Dispose() { lock (gate) { disposed = true; Cancellation.Dispose(); } }
    }
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[8192];
        using var frame = new MemoryStream();
        var oversized = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var count = await input.ReadAsync(buffer, stop.Token);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        if (!oversized && frame.Length < MaxFrameBytes) frame.WriteByte(buffer[i]);
                        else oversized = true;
                        continue;
                    }
                    if (oversized) await ErrorAsync(output, null, "FRAME_LIMIT", "The request exceeds the frame limit.");
                    else if (frame.Length > 0 && await DispatchAsync(frame.ToArray(), output)) { stop.Cancel(); break; }
                    frame.SetLength(0); oversized = false;
                }
            }
            if (!stop.IsCancellationRequested && (frame.Length > 0 || oversized))
                await ErrorAsync(output, null, "INVALID_FRAME", "Requests must end with a newline.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            // Close the service before drain, but let publishers finish their terminal writers
            // and dispose leases concurrently; awaiting service first would wait on those leases.
            var effectsClosing = itemEffects?.DisposeAsync().AsTask();
            var geometryClosing = geometryLeases?.DisposeAsync().AsTask();
            try
            {
                var retiring = active.Values.ToArray();
                foreach (var request in retiring) request.Cancel();
                await Task.WhenAll(retiring.Select(request => request.Retired.Task));
            }
            finally
            {
                await Task.WhenAll(new[] { effectsClosing, geometryClosing }.OfType<Task>());
                if (geometryCleanupFailure is not null) throw geometryCleanupFailure;
                if (effectsCleanupFailure is not null) throw effectsCleanupFailure;
            }
        }
    }
    private async Task<bool> DispatchAsync(byte[] bytes, Stream output)
    {
        string? id = null;
        JsonElement parameters;
        string method;
        try
        {
            // JsonDocument accepts only strict UTF-8. Duplicate top-level/parameter names are rejected.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RequireObject(root);
            RequireOnly(root, "protocolVersion", "id", "method", "params");
            if (root.TryGetProperty("id", out var identity) && identity.ValueKind == JsonValueKind.String) id = identity.GetString();
            if (!root.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var integer) || integer != 1)
                throw new DiscoveryException("PROTOCOL_VERSION", "Protocol version 1 is required.");
            if (id is null || id.Length is < 1 or > 128 || id.Any(char.IsControl))
                throw new DiscoveryException("INVALID_REQUEST", "A bounded request ID is required.");
            if (!root.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String)
                throw new DiscoveryException("INVALID_REQUEST", "A request method is required.");
            method = methodValue.GetString()!;
            if (!root.TryGetProperty("params", out parameters)) throw new DiscoveryException("INVALID_REQUEST", "Request parameters are required.");
            RequireObject(parameters);
            parameters = parameters.Clone();
        }
        catch (JsonException) { await ErrorAsync(output, id, "INVALID_REQUEST", "The request is not valid JSON."); return false; }
        catch (DiscoveryException ex) { await ErrorAsync(output, id, ex.Code, ex.Message, ex.Retryable, definition: ex.Definition); return false; }
        catch (InvalidOperationException) { await ErrorAsync(output, id, "INVALID_REQUEST", "The request shape is invalid."); return false; }
        if (active.ContainsKey(id)) { await ErrorAsync(output, id, "DUPLICATE_ID", "This request ID is already active."); return false; }
        if (method == "shutdown")
        {
            if (parameters.EnumerateObject().Any()) { await ErrorAsync(output, id, "INVALID_REQUEST", "Shutdown takes no parameters."); return false; }
            await ReplyAsync(output, id, new { stopping = true });
            return true;
        }
        if (method == "request.cancel")
        {
            try
            {
                RequireOnly(parameters, "requestId");
                var targetId = RequiredString(parameters, "requestId");
                var requested = active.TryGetValue(targetId, out var target);
                if (requested) target!.Cancel();
                await ReplyAsync(output, id, new { requested });
            }
            catch (DiscoveryException ex) { await ErrorAsync(output, id, ex.Code, ex.Message, definition: ex.Definition); }
            return false;
        }
        if (active.Count >= 32) { await ErrorAsync(output, id, "BUSY", "The engine has reached its active request limit.", true); return false; }
        var request = new ActiveRequest();
        if (!active.TryAdd(id, request)) { request.Dispose(); await ErrorAsync(output, id, "DUPLICATE_ID", "This request ID is already active."); return false; }
        // Work observes all exceptions internally and signals retirement; the dictionary bounds its lifetime.
        _ = ExecuteAsync(id, method, parameters, request, output);
        return false;
    }
    private async Task ExecuteAsync(string id, string method, JsonElement parameters, ActiveRequest request, Stream output)
    {
        var frameKind = method is "creatures.geometry.read" or "creatures.geometry.prepare" or "creatures.geometry.open" ? FrameKind.CreatureGeometry :
            method.StartsWith("items.effects.", StringComparison.Ordinal) ? FrameKind.ItemEffects :
            method is "creatures.data" or "creatures.prepare" or "creatures.page" or "creatures.inspect" or "creatures.display.read" or "creatures.display.prepare" ? FrameKind.Creatures :
            method == "items.classifications" ? FrameKind.Classifications : FrameKind.General;
        var maximumBytes = frameKind == FrameKind.CreatureGeometry ? MaxCreatureGeometryFrameBytes : frameKind == FrameKind.General ? MaxFrameBytes : MaxItemEffectsFrameBytes;
        string? pendingGeometryLease = null;
        ItemEffectsOperationLease? effectsLease = null;
        var classificationPublicationHeld = false;
        var creaturesPublicationHeld = false;
        var creaturesPreparationHeld = false;
        try
        {
            object result;
            switch (method)
            {
                case "hello":
                    RequireOnly(parameters);
                    var capabilities = new List<string> { "discovery-v1", "discovery-all-products-v1", "local-history", "cancellation", "home-download-v1", "download-v1", "inventory-v1", "inventory-files-v1", "file-catalog-v1", "tables-definitions-v1", "tables-data-v1" };
                    if (tableData is not null && referenceText is not null) capabilities.Add("tables-reference-text-v1");
                    if (tableData is not null && follow is not null) capabilities.Add("tables-follow-v1");
                    if (retention is not null) capabilities.Add("retention-v1");
                    if (settings is not null) capabilities.Add("settings-v1");
                    if (settings is not null && listfile is not null) capabilities.Add("community-listfile-v1");
                    if (preparedData is not null) capabilities.Add("prepared-data-v1");
                    if (downloadSummaries is not null) capabilities.Add("download-summary-v1");
                    if (downloads is not null) capabilities.Add("download-check-v1");
                    if (items is not null) { capabilities.Add("items-v1"); capabilities.Add("item-facts-v1"); }
                    if (itemIcons is not null && iconLeases is not null) capabilities.Add("item-icons-v1");
                    if (itemClassifications is not null) capabilities.Add("item-classifications-v1");
                    if (itemClassifications?.SupportsSubclassLabels == true) capabilities.Add("item-subclass-labels-v1");
                    if (creatures is not null) capabilities.Add("creatures-v1");
                    if (creatures is not null && creatureDisplays is not null) capabilities.Add("creature-display-metadata-v1");
                    if (creatures is not null && creatureDisplays is not null && creatureGeometry is not null && geometryLeases is not null)
                        capabilities.Add("creature-source-geometry-v1");
                    // Ordinary Program has no effects service until all three real phases qualify.
                    if (itemEffects is not null) capabilities.AddRange(["item-effects-links-v1", "item-effects-inputs-v1", "item-effect-spell-names-v1"]);
                    result = new { protocolVersion = 1, engineVersion = "0.1.0", capabilities };
                    break;
                case "products.list":
                    RequireOnly(parameters);
                    result = new { products = ProductRegistry.Products };
                    break;
                case "discovery.read":
                    RequireOnly(parameters, "productId", "filter", "limit", "cursor");
                    var filter = OptionalString(parameters, "filter") ?? "all";
                    var cursor = OptionalString(parameters, "cursor");
                    var limit = 50;
                    if (parameters.TryGetProperty("limit", out var count) && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out limit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The page size must be an integer.");
                    result = await service.ReadAsync(RequiredString(parameters, "productId"), filter, limit, cursor, request.Cancellation.Token);
                    break;
                case "discovery.refresh":
                    RequireOnly(parameters, "productId");
                    var product = RequiredString(parameters, "productId"); ProductRegistry.Validate(product);
                    await WriteAsync(output, new { protocolVersion = 1, type = "progress", requestId = id,
                        sequence = 1, stage = "Downloading metadata", region = (string?)null, completed = (int?)null, total = (int?)null, unit = (string?)null });
                    result = await service.RefreshAsync(product, request.Cancellation.Token);
                    break;
                case "discovery.readAll":
                    RequireOnly(parameters, "productId", "filter", "limit", "cursor");
                    var allLimit = 50;
                    if (parameters.TryGetProperty("limit", out var allCount) && (allCount.ValueKind != JsonValueKind.Number || !allCount.TryGetInt32(out allLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The page size must be an integer.");
                    result = await service.ReadAllAsync(OptionalString(parameters, "productId"), OptionalString(parameters, "filter") ?? "all",
                        allLimit, OptionalString(parameters, "cursor"), request.Cancellation.Token);
                    break;
                case "discovery.refreshAll":
                    RequireOnly(parameters);
                    long refreshSequence = 0;
                    // The service reports one product at a time, so sequence order matches the completed count.
                    result = await service.RefreshAllAsync(progress => WriteAsync(output, new { protocolVersion = 1, type = "progress", requestId = id,
                        sequence = Interlocked.Increment(ref refreshSequence), stage = "Checking official metadata", region = (string?)null,
                        completed = progress.Completed, total = progress.Total, unit = "products" }), request.Cancellation.Token);
                    break;
                case "tables.catalog":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "query", "limit", "after");
                    if (tables is null) throw new DiscoveryException("TABLES_UNAVAILABLE", "Table definitions are unavailable.");
                    var tableLimit = 50;
                    if (parameters.TryGetProperty("limit", out var tablePageSize) && (tablePageSize.ValueKind != JsonValueKind.Number || !tablePageSize.TryGetInt32(out tableLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The table catalogue page size must be an integer.");
                    var tableQuery = "";
                    if (parameters.TryGetProperty("query", out var queryValue))
                    {
                        if (queryValue.ValueKind != JsonValueKind.String)
                            throw new DiscoveryException("INVALID_REQUEST", "The table catalogue query must be text.");
                        tableQuery = queryValue.GetString()!;
                    }
                    result = await tables.ReadCatalogAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"), RequiredString(parameters, "sourceRegion"),
                        tableQuery, tableLimit, OptionalString(parameters, "after")), request.Cancellation.Token);
                    break;
                case "tables.definition":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "tableId");
                    if (tables is null) throw new DiscoveryException("TABLES_UNAVAILABLE", "Table definitions are unavailable.");
                    result = await tables.ReadDefinitionAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"), RequiredString(parameters, "sourceRegion"),
                        RequiredString(parameters, "tableId")), request.Cancellation.Token);
                    break;
                case "tables.prepare":
                case "tables.data":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "tableId", "researchLocale");
                    if (tableData is null) throw new DiscoveryException("TABLE_DATA_UNAVAILABLE", "Table data loading is unavailable.");
                    var dataRequest = new TableDataRequest(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion"), RequiredString(parameters, "tableId"), RequiredString(parameters, "researchLocale"),
                        FetchedDefinitions: true); // Tables chooses by D081; features do the same (D101).
                    long dataSequence = 0;
                    result = method == "tables.data"
                        ? await tableData.ReadAsync(dataRequest, request.Cancellation.Token)
                        : await PrepareTableAsync(dataRequest, stage => WriteAsync(output, new { protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref dataSequence), stage, region = dataRequest.SourceRegion,
                            completed = (int?)null, total = (int?)null, unit = (string?)null }), request.Cancellation.Token);
                    break;
                case "tables.rows":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "tableId", "researchLocale", "artifactId", "limit", "after", "recordId");
                    if (tableData is null) throw new DiscoveryException("TABLE_DATA_UNAVAILABLE", "Table data loading is unavailable.");
                    var rowLimit = 50;
                    if (parameters.TryGetProperty("limit", out var rowPageSize) && (rowPageSize.ValueKind != JsonValueKind.Number || !rowPageSize.TryGetInt32(out rowLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The table row page size must be an integer.");
                    result = await tableData.ReadRowsAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion"), RequiredString(parameters, "tableId"), RequiredString(parameters, "researchLocale"),
                        RequiredString(parameters, "artifactId"), rowLimit, OptionalString(parameters, "after"), OptionalString(parameters, "recordId")), request.Cancellation.Token);
                    break;
                case "tables.referenceText":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "tableId", "researchLocale", "artifactId", "limit", "after", "recordId");
                    if (tableData is null || referenceText is null) throw new DiscoveryException("TABLE_REFERENCE_TEXT_UNAVAILABLE", "Reference text is unavailable.");
                    var textLimit = 50;
                    if (parameters.TryGetProperty("limit", out var textPageSize) && (textPageSize.ValueKind != JsonValueKind.Number || !textPageSize.TryGetInt32(out textLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The table row page size must be an integer.");
                    result = await referenceText.ReadAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion"), RequiredString(parameters, "tableId"), RequiredString(parameters, "researchLocale"),
                        RequiredString(parameters, "artifactId"), textLimit, OptionalString(parameters, "after"), OptionalString(parameters, "recordId")), request.Cancellation.Token);
                    break;
                case "tables.follow":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "tableId", "researchLocale", "artifactId", "columnIndex", "value", "targetTableId", "targetArtifactId", "limit", "after");
                    if (tableData is null || follow is null) throw new DiscoveryException("TABLE_FOLLOW_UNAVAILABLE", "Following references is unavailable.");
                    var followLimit = 50;
                    if (parameters.TryGetProperty("limit", out var followPageSize) && (followPageSize.ValueKind != JsonValueKind.Number || !followPageSize.TryGetInt32(out followLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The follow page size must be an integer.");
                    if (!parameters.TryGetProperty("columnIndex", out var followColumn) || followColumn.ValueKind != JsonValueKind.Number || !followColumn.TryGetInt32(out var followIndex))
                        throw new DiscoveryException("INVALID_REQUEST", "The followed column must be an integer index.");
                    result = await follow.FollowAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion"), RequiredString(parameters, "tableId"), RequiredString(parameters, "researchLocale"),
                        RequiredString(parameters, "artifactId"), followIndex, RequiredString(parameters, "value"), RequiredString(parameters, "targetTableId"),
                        RequiredString(parameters, "targetArtifactId"), followLimit, OptionalString(parameters, "after")), request.Cancellation.Token);
                    break;
                case "inventory.read":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "generation", "limit", "after");
                    if (inventory is null) throw new DiscoveryException("INVENTORY_UNAVAILABLE", "Inventory queries are unavailable.");
                    var pageLimit = 50;
                    if (parameters.TryGetProperty("limit", out var pageSize) && (pageSize.ValueKind != JsonValueKind.Number || !pageSize.TryGetInt32(out pageLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The page size must be an integer.");
                    result = await inventory.ReadAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"), RequiredString(parameters, "sourceRegion"),
                        RequiredString(parameters, "generation"), pageLimit, OptionalString(parameters, "after")), request.Cancellation.Token);
                    break;
                case "inventory.catalog":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion");
                    if (inventory is null) throw new DiscoveryException("INVENTORY_UNAVAILABLE", "Inventory queries are unavailable.");
                    result = await inventory.ReadCatalogAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion")), request.Cancellation.Token);
                    break;
                case "inventory.catalogs":
                    RequireOnly(parameters);
                    if (inventory is null) throw new DiscoveryException("INVENTORY_UNAVAILABLE", "Inventory queries are unavailable.");
                    result = await inventory.ListCatalogsAsync(request.Cancellation.Token);
                    break;
                case "inventory.catalog.set":
                    RequireOnly(parameters, "productId", "buildIdentity", "action");
                    if (inventory is null) throw new DiscoveryException("INVENTORY_UNAVAILABLE", "Inventory queries are unavailable.");
                    result = await inventory.SetCatalogAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "action")), request.Cancellation.Token);
                    break;
                case "inventory.files":
                case "inventory.file":
                    {
                        var single = method == "inventory.file";
                        RequireOnly(parameters, [.. InventoryContextNames, .. InventoryFilterNames, .. (single ? new[] { "file" } : [])]);
                        if (inventory is null) throw new DiscoveryException("INVENTORY_UNAVAILABLE", "Inventory queries are unavailable.");
                        var filesLimit = single ? InventoryService.MaxMembershipsPage : 50;
                        if (parameters.TryGetProperty("limit", out var filesSize) && (filesSize.ValueKind != JsonValueKind.Number || !filesSize.TryGetInt32(out filesLimit)))
                            throw new DiscoveryException("INVALID_REQUEST", "The page size must be an integer.");
                        var storedOnly = false;
                        if (parameters.TryGetProperty("storedOnly", out var storedValue))
                        {
                            if (storedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                throw new DiscoveryException("INVALID_REQUEST", "The storedOnly filter must be a boolean.");
                            storedOnly = storedValue.GetBoolean();
                        }
                        var fileFilter = new InventoryFileFilter(OptionalString(parameters, "fileDataId"), OptionalString(parameters, "contentKey"), OptionalString(parameters, "localeFlags"),
                            OptionalString(parameters, "contentFlags"), OptionalString(parameters, "family"), storedOnly);
                        string fileProduct = RequiredString(parameters, "productId"), fileBuild = RequiredString(parameters, "buildIdentity"),
                            fileRegion = RequiredString(parameters, "sourceRegion"), fileGeneration = RequiredString(parameters, "generation");
                        result = single
                            ? await inventory.ReadFileAsync(new(fileProduct, fileBuild, fileRegion, fileGeneration, RequiredString(parameters, "file"), fileFilter, filesLimit, OptionalString(parameters, "after")), request.Cancellation.Token)
                            : await inventory.ReadFilesAsync(new(fileProduct, fileBuild, fileRegion, fileGeneration, fileFilter, filesLimit, OptionalString(parameters, "after")), request.Cancellation.Token);
                    }
                    break;
                case "download.read":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "ledgerId", "limit", "after", "from");
                    if (downloads is null) throw new DiscoveryException("DOWNLOAD_UNAVAILABLE", "Download-object queries are unavailable.");
                    var downloadLimit = 50;
                    if (parameters.TryGetProperty("limit", out var downloadSize) && (downloadSize.ValueKind != JsonValueKind.Number || !downloadSize.TryGetInt32(out downloadLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The page size must be an integer.");
                    result = await downloads.ReadAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"), RequiredString(parameters, "sourceRegion"),
                        RequiredString(parameters, "ledgerId"), downloadLimit, OptionalString(parameters, "after"), OptionalString(parameters, "from")), request.Cancellation.Token);
                    break;
                case "download.check":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "ledgerId");
                    if (downloads is null) throw new DiscoveryException("DOWNLOAD_UNAVAILABLE", "Download-object queries are unavailable.");
                    var check = new DownloadCheckRequest(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion"), RequiredString(parameters, "ledgerId"));
                    long checkSequence = 0;
                    result = await downloads.CheckAsync(check, progress => WriteAsync(output, new
                    {
                        protocolVersion = 1, type = "progress", requestId = id, sequence = Interlocked.Increment(ref checkSequence),
                        stage = "Checking files not checked at Open", region = check.SourceRegion, completed = progress.Completed, total = progress.Total, unit = "objects"
                    }), request.Cancellation.Token);
                    break;
                case "download.summaries":
                    RequireOnly(parameters, "builds");
                    if (downloadSummaries is null) throw new DiscoveryException("DOWNLOAD_SUMMARY_UNAVAILABLE", "Stored download figures are unavailable.");
                    if (!parameters.TryGetProperty("builds", out var summaryBuilds) || summaryBuilds.ValueKind != JsonValueKind.Array ||
                        summaryBuilds.GetArrayLength() is < 1 or > DownloadSummaryService.MaxBuilds)
                        throw new DiscoveryException("INVALID_REQUEST", "Choose one to 100 distinct exact builds.");
                    var summaryKeys = summaryBuilds.EnumerateArray().Select(item =>
                    {
                        if (item.ValueKind != JsonValueKind.Object) throw new DiscoveryException("INVALID_REQUEST", "Each build is an exact product and identity.");
                        RequireOnly(item, "productId", "buildIdentity");
                        return new BuildKey(RequiredString(item, "productId"), RequiredString(item, "buildIdentity"));
                    }).ToArray();
                    var summaryRevision = downloadSummaries.Revision;
                    result = new { summaries = (await downloadSummaries.ReadAsync(summaryKeys, request.Cancellation.Token)).Select(DownloadSummaryWire.Summary).ToArray(), revision = summaryRevision };
                    break;
                case "download.changes":
                    RequireOnly(parameters, "after");
                    if (downloadSummaries is null) throw new DiscoveryException("DOWNLOAD_SUMMARY_UNAVAILABLE", "Stored download figures are unavailable.");
                    result = new { revision = await downloadSummaries.WaitAsync(RequiredString(parameters, "after"), request.Cancellation.Token) };
                    break;
                case "items.classifications":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "itemIds", "includeSubclass", "policyRevision");
                    if (itemClassifications is null) throw new DiscoveryException("ITEM_CLASSIFICATIONS_UNAVAILABLE", "Item classification labels are unavailable.");
                    var classificationContext = ItemsContextOf(parameters);
                    if (!parameters.TryGetProperty("itemIds", out var classificationIds) || classificationIds.ValueKind != JsonValueKind.Array || classificationIds.GetArrayLength() is < 1 or > 50)
                        throw new DiscoveryException("INVALID_REQUEST", "Choose one to fifty admitted Item IDs for classification labels.");
                    var classificationItemIds = classificationIds.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? RequestString(value)
                        : throw new DiscoveryException("INVALID_REQUEST", "Classification Item IDs must be canonical decimal strings.")).ToArray();
                    var includeSubclass = false;
                    if (parameters.TryGetProperty("includeSubclass", out var subclassValue))
                    {
                        if (subclassValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new DiscoveryException("INVALID_REQUEST", "The includeSubclass parameter must be a boolean.");
                        includeSubclass = subclassValue.GetBoolean();
                    }
                    long classificationSequence = 0;
                    var classificationPolicy = OptionalString(parameters, "policyRevision") ?? "classification-label-index-v1";
                    var classificationRequest = new ItemClassificationBatchRequest(classificationContext,
                        RequiredString(parameters, "artifactId"), classificationItemIds, includeSubclass, classificationPolicy);
                    ItemClassificationsService.Validate(classificationRequest);
                    if (classificationPolicy == "classification-label-index-v2" && !itemClassifications.SupportsSubclassLabels)
                        throw new DiscoveryException("ITEM_SUBCLASS_UNSUPPORTED", "This classification service does not support the requested v2 policy.");
                    // Bound detached domain results as well as encoded frames. A permit
                    // survives application-consumer completion until terminal stdout flush.
                    await classificationPublication.WaitAsync(request.Cancellation.Token);
                    classificationPublicationHeld = true;
                    result = ItemClassificationsWire.Batch(await itemClassifications.PrepareBatchAsync(classificationRequest, progress => WriteAsync(output, new
                        {
                            protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref classificationSequence), stage = progress.Stage,
                            region = classificationContext.SourceRegion, completed = progress.Completed, total = progress.Total, unit = progress.Unit
                        }), request.Cancellation.Token));
                    break;
                case "items.icons":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "itemIds");
                    if (itemIcons is null || iconLeases is null) throw new DiscoveryException("ITEM_ICONS_UNAVAILABLE", "Item icons are unavailable.");
                    var iconContext = ItemsContextOf(parameters);
                    if (!parameters.TryGetProperty("itemIds", out var iconIds) || iconIds.ValueKind != JsonValueKind.Array || iconIds.GetArrayLength() is < 1 or > 50)
                        throw new DiscoveryException("INVALID_REQUEST", "Choose at most fifty admitted Item IDs for icons.");
                    var iconItemIds = iconIds.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? RequestString(value)
                        : throw new DiscoveryException("INVALID_REQUEST", "Icon Item IDs must be canonical decimal strings.")).ToArray();
                    long iconSequence = 0;
                    result = ItemIconsWire.Batch(await itemIcons.PrepareBatchAsync(new(iconContext, RequiredString(parameters, "artifactId"), iconItemIds),
                        progress => WriteAsync(output, new
                        {
                            protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref iconSequence), stage = progress.Stage,
                            region = iconContext.SourceRegion, completed = progress.Completed, total = progress.Total, unit = progress.Unit
                        }), request.Cancellation.Token));
                    break;
                case "items.icon.open":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "itemId", "imageArtifactId");
                    if (itemIcons is null || iconLeases is null) throw new DiscoveryException("ITEM_ICONS_UNAVAILABLE", "Item icons are unavailable.");
                    result = await iconLeases.OpenAsync(new(ItemsContextOf(parameters), RequiredString(parameters, "artifactId"),
                        RequiredString(parameters, "itemId"), RequiredString(parameters, "imageArtifactId")), request.Cancellation.Token);
                    break;
                case "items.icon.close":
                    RequireOnly(parameters, "leaseId");
                    if (iconLeases is null) throw new DiscoveryException("ITEM_ICONS_UNAVAILABLE", "Item icons are unavailable.");
                    await iconLeases.CloseAsync(RequiredString(parameters, "leaseId"));
                    result = new { closed = true };
                    break;
                case "items.effects.data":
                case "items.effects.prepare":
                case "items.effects.page":
                case "items.effects.details":
                case "items.effects.details.prepare":
                case "items.effects.names":
                case "items.effects.names.prepare":
                    var effectsRequest = ItemEffectsWire.Request(method, parameters);
                    if (itemEffects is null) throw new DiscoveryException("ITEM_EFFECTS_UNAVAILABLE", "Selected Item effects are unavailable.");
                    effectsLease = itemEffects.Admit(effectsRequest.Operation);
                    long effectsSequence = 0;
                    Task ReportEffects(ItemEffectsProgress progress) => WriteAsync(output, new
                    {
                        protocolVersion = 1, type = "progress", requestId = id,
                        sequence = Interlocked.Increment(ref effectsSequence), stage = progress.Stage,
                        region = effectsRequest.Selection.Context.SourceRegion, completed = progress.Completed, total = progress.Total,
                        unit = progress.Unit, tableId = progress.TableId
                    }, MaxItemEffectsFrameBytes, FrameKind.ItemEffects);
                    result = effectsRequest.Operation switch
                    {
                        ItemEffectsOperation.Read => ItemEffectsWire.Readiness(await itemEffects.ReadAsync(effectsLease, effectsRequest.Selection, request.Cancellation.Token)),
                        ItemEffectsOperation.PrepareLinks => ItemEffectsWire.Readiness(await itemEffects.PrepareLinksAsync(effectsLease, effectsRequest.Selection, ReportEffects, request.Cancellation.Token)),
                        ItemEffectsOperation.ReadPage => ItemEffectsWire.Page(await itemEffects.ReadPageAsync(effectsLease, effectsRequest.Page!, request.Cancellation.Token)),
                        ItemEffectsOperation.ReadDetails => ItemEffectsWire.Details(await itemEffects.ReadDetailsAsync(effectsLease, effectsRequest.Details!, request.Cancellation.Token)),
                        ItemEffectsOperation.PrepareDetails => ItemEffectsWire.Details(await itemEffects.PrepareDetailsAsync(effectsLease, effectsRequest.Details!, ReportEffects, request.Cancellation.Token)),
                        ItemEffectsOperation.ReadNames => ItemEffectsWire.Names(await itemEffects.ReadNamesAsync(effectsLease, effectsRequest.Names!, request.Cancellation.Token)),
                        ItemEffectsOperation.PrepareNames => ItemEffectsWire.Names(await itemEffects.PrepareNamesAsync(effectsLease, effectsRequest.Names!, ReportEffects, request.Cancellation.Token)),
                        _ => throw new DiscoveryException("INVALID_REQUEST", "Choose one defined effects operation.")
                    };
                    break;
                case "creatures.geometry.close":
                    var closeGeometry = CreatureGeometryWire.Request(method, parameters);
                    if (geometryLeases is null) throw new DiscoveryException("CREATURE_GEOMETRY_UNAVAILABLE", "Selected Creature geometry is unavailable.");
                    await geometryLeases.CloseAsync(closeGeometry.LeaseId!);
                    result = new { closed = true };
                    break;
                case "creatures.geometry.read":
                case "creatures.geometry.prepare":
                case "creatures.geometry.open":
                    var geometryRequest = CreatureGeometryWire.Request(method, parameters);
                    if (creatureGeometry is null || geometryLeases is null)
                        throw new DiscoveryException("CREATURE_GEOMETRY_UNAVAILABLE", "Selected Creature geometry is unavailable.");
                    if (geometryRequest.Prepare is not null)
                    {
                        if (!await creaturesPreparation.WaitAsync(0)) throw new DiscoveryException("CREATURES_BUSY", "Creature preparations are at their bounded capacity.", true);
                        creaturesPreparationHeld = true;
                    }
                    if (!await creaturesPublication.WaitAsync(0)) throw CreaturesQueryBusy();
                    creaturesPublicationHeld = true;
                    if (geometryRequest.Open is { } geometryOpen)
                    {
                        var descriptor = await geometryLeases.OpenAsync(geometryOpen, request.Cancellation.Token);
                        pendingGeometryLease = descriptor.LeaseId;
                        result = CreatureGeometryWire.Descriptor(descriptor);
                    }
                    else if (geometryRequest.Read is { } geometryRead)
                        result = CreatureGeometryWire.Result(await creatureGeometry.ReadAsync(geometryRead, request.Cancellation.Token));
                    else
                    {
                        long geometrySequence = 0;
                        var prepareGeometry = geometryRequest.Prepare!;
                        result = CreatureGeometryWire.Result(await creatureGeometry.PrepareAsync(prepareGeometry, progress => WriteAsync(output, new
                        {
                            protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref geometrySequence), stage = progress.Label,
                            region = prepareGeometry.Selection.Context.SourceRegion, completed = progress.Completed, total = progress.Total, unit = progress.Unit,
                            geometryStage = progress.Stage, geometryDependency = progress.Dependency is { } dependency ? CreatureGeometryWire.Name(dependency) : null
                        }, MaxCreatureGeometryFrameBytes, FrameKind.CreatureGeometry), request.Cancellation.Token));
                    }
                    break;
                case "creatures.display.read":
                case "creatures.display.prepare":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "creatureId", "slot");
                    if (creatures is null || creatureDisplays is null)
                        throw new DiscoveryException("CREATURE_DISPLAY_UNAVAILABLE", "Selected Creature display metadata is unavailable.");
                    if (!parameters.TryGetProperty("slot", out var slotValue) || slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetInt32(out var selectedSlot))
                        throw new DiscoveryException("INVALID_REQUEST", "An integer Creature display slot is required.");
                    var displayRequest = new CreatureDisplayMetadataRequest(CreaturesContextOf(parameters), RequiredString(parameters, "artifactId"),
                        RequiredString(parameters, "creatureId"), selectedSlot);
                    CreatureDisplayMetadataService.Validate(displayRequest);
                    if (method == "creatures.display.prepare")
                    {
                        if (!await creaturesPreparation.WaitAsync(0))
                            throw new DiscoveryException("CREATURES_BUSY", "Creature preparation requests are at their bounded publication capacity.", true);
                        creaturesPreparationHeld = true;
                    }
                    if (!await creaturesPublication.WaitAsync(0)) throw CreaturesQueryBusy();
                    creaturesPublicationHeld = true;
                    long displaySequence = 0;
                    result = CreatureDisplayWire.Result(method == "creatures.display.read"
                        ? await creatureDisplays.ReadDisplayAsync(displayRequest, request.Cancellation.Token)
                        : await creatureDisplays.PrepareDisplayAsync(displayRequest, progress => WriteAsync(output, new
                        {
                            protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref displaySequence), stage = progress.Stage,
                            region = displayRequest.Context.SourceRegion, completed = progress.Completed, total = progress.Total, unit = progress.Unit
                        }), request.Cancellation.Token));
                    break;
                case "creatures.data":
                case "creatures.prepare":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale");
                    if (creatures is null) throw new DiscoveryException("CREATURES_UNAVAILABLE", "Creature browsing is unavailable.");
                    var creatureContext = CreaturesContextOf(parameters);
                    CreaturesService.Validate(creatureContext);
                    if (method == "creatures.prepare")
                    {
                        if (!await creaturesPreparation.WaitAsync(0))
                            throw new DiscoveryException("CREATURES_BUSY", "Creature preparation requests are at their bounded publication capacity.", true);
                        creaturesPreparationHeld = true;
                    }
                    if (!await creaturesPublication.WaitAsync(0)) throw CreaturesQueryBusy();
                    creaturesPublicationHeld = true;
                    long creatureSequence = 0;
                    result = method == "creatures.data" ? CreaturesWire.Readiness(await creatures.ReadAsync(creatureContext, request.Cancellation.Token))
                        : CreaturesWire.Snapshot(await creatures.PrepareAsync(creatureContext, progress => WriteAsync(output, new
                        {
                            protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref creatureSequence), stage = progress.Stage,
                            region = creatureContext.SourceRegion, completed = progress.Completed, total = progress.Total, unit = progress.Unit
                        }), request.Cancellation.Token));
                    break;
                case "creatures.page":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "query", "exactCreatureId", "limit", "after");
                    if (creatures is null) throw new DiscoveryException("CREATURES_UNAVAILABLE", "Creature browsing is unavailable.");
                    var creatureLimit = 50;
                    if (parameters.TryGetProperty("limit", out var creatureSize) && (creatureSize.ValueKind != JsonValueKind.Number || !creatureSize.TryGetInt32(out creatureLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The Creature page size must be an integer.");
                    var creatureQuery = "";
                    if (parameters.TryGetProperty("query", out var creatureQueryValue))
                    {
                        if (creatureQueryValue.ValueKind != JsonValueKind.String) throw new DiscoveryException("INVALID_REQUEST", "The Creature query must be literal text.");
                        creatureQuery = RequestString(creatureQueryValue);
                    }
                    var creaturePage = new CreaturesPageRequest(CreaturesContextOf(parameters), RequiredString(parameters, "artifactId"),
                        creatureQuery, OptionalString(parameters, "exactCreatureId"), creatureLimit, OptionalString(parameters, "after"));
                    CreaturesService.Validate(creaturePage);
                    if (!await creaturesPublication.WaitAsync(0)) throw CreaturesQueryBusy();
                    creaturesPublicationHeld = true;
                    result = CreaturesWire.Page(await creatures.ReadPageAsync(creaturePage, request.Cancellation.Token));
                    break;
                case "creatures.inspect":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "creatureId");
                    if (creatures is null) throw new DiscoveryException("CREATURES_UNAVAILABLE", "Creature browsing is unavailable.");
                    var creatureInspect = new CreatureInspectRequest(CreaturesContextOf(parameters), RequiredString(parameters, "artifactId"), RequiredString(parameters, "creatureId"));
                    CreaturesService.Validate(creatureInspect);
                    if (!await creaturesPublication.WaitAsync(0)) throw CreaturesQueryBusy();
                    creaturesPublicationHeld = true;
                    result = CreaturesWire.Inspect(await creatures.InspectAsync(creatureInspect, request.Cancellation.Token));
                    break;
                case "items.data":
                case "items.prepare":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale");
                    if (items is null) throw new DiscoveryException("ITEMS_UNAVAILABLE", "Items browsing is unavailable.");
                    var itemsContext = ItemsContextOf(parameters);
                    long itemsSequence = 0;
                    result = method == "items.data" ? await items.ReadAsync(itemsContext, request.Cancellation.Token)
                        : await items.PrepareAsync(itemsContext, progress => WriteAsync(output, new
                        {
                            protocolVersion = 1, type = "progress", requestId = id,
                            sequence = Interlocked.Increment(ref itemsSequence), stage = progress.Stage,
                            region = itemsContext.SourceRegion, completed = progress.Completed,
                            total = progress.Total, unit = progress.Unit
                        }), request.Cancellation.Token);
                    break;
                case "items.page":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "query", "exactItemId", "limit", "after");
                    if (items is null) throw new DiscoveryException("ITEMS_UNAVAILABLE", "Items browsing is unavailable.");
                    var itemsLimit = 50;
                    if (parameters.TryGetProperty("limit", out var itemsPageSize) &&
                        (itemsPageSize.ValueKind != JsonValueKind.Number || !itemsPageSize.TryGetInt32(out itemsLimit)))
                        throw new DiscoveryException("INVALID_REQUEST", "The Items page size must be an integer.");
                    var itemsQuery = "";
                    if (parameters.TryGetProperty("query", out var itemsQueryValue))
                    {
                        if (itemsQueryValue.ValueKind != JsonValueKind.String)
                            throw new DiscoveryException("INVALID_REQUEST", "The Items query must be literal text.");
                        itemsQuery = RequestString(itemsQueryValue);
                    }
                    result = ItemsWire.Page(await items.ReadPageAsync(new(ItemsContextOf(parameters), RequiredString(parameters, "artifactId"),
                        itemsQuery, OptionalString(parameters, "exactItemId"), itemsLimit, OptionalString(parameters, "after")), request.Cancellation.Token));
                    break;
                case "items.inspect":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "itemId");
                    if (items is null) throw new DiscoveryException("ITEMS_UNAVAILABLE", "Items browsing is unavailable.");
                    result = ItemsWire.Inspect(await items.InspectAsync(new(ItemsContextOf(parameters), RequiredString(parameters, "artifactId"),
                        RequiredString(parameters, "itemId")), request.Cancellation.Token));
                    break;
                case "items.facts":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "researchLocale", "artifactId", "itemId");
                    if (items is null) throw new DiscoveryException("ITEMS_UNAVAILABLE", "Items browsing is unavailable.");
                    result = ItemsWire.Facts(await items.FactsAsync(new(ItemsContextOf(parameters), RequiredString(parameters, "artifactId"),
                        RequiredString(parameters, "itemId")), request.Cancellation.Token));
                    break;
                case "items.labels":
                    RequireOnly(parameters);
                    if (items is null) throw new DiscoveryException("ITEMS_UNAVAILABLE", "Items browsing is unavailable.");
                    result = ItemsWire.Labels(items.Labels());
                    break;
                case "retention.list":
                    RequireOnly(parameters);
                    if (retention is null) throw new DiscoveryException("RETENTION_UNAVAILABLE", "Build retention is unavailable.");
                    result = new { jobs = (await retention.ListAsync(request.Cancellation.Token)).Select(RetentionWire.Snapshot).ToArray() };
                    break;
                case "retention.read":
                case "retention.set":
                    if (method == "retention.set") RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "ledgerId", "action", "expectedRevision");
                    else RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion", "ledgerId");
                    if (retention is null) throw new DiscoveryException("RETENTION_UNAVAILABLE", "Build retention is unavailable.");
                    var retentionTarget = new RetentionTarget(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"),
                        RequiredString(parameters, "sourceRegion"), RequiredString(parameters, "ledgerId"));
                    var expectedRevision = method == "retention.set" ? OptionalString(parameters, "expectedRevision") : null;
                    result = RetentionWire.Snapshot(method == "retention.read"
                        ? await retention.ReadAsync(retentionTarget, request.Cancellation.Token)
                        : await retention.SetAsync(new(retentionTarget, RetentionWire.Action(RequiredString(parameters, "action")),
                            expectedRevision is null ? null : RetentionWire.Revision(expectedRevision)), request.Cancellation.Token));
                    break;
                case "settings.read":
                    RequireOnly(parameters);
                    if (settings is null) throw new DiscoveryException("SETTINGS_UNAVAILABLE", "Settings are unavailable.");
                    result = SettingsWire.Snapshot(await settings.ReadAsync(request.Cancellation.Token));
                    break;
                case "settings.set":
                    RequireOnly(parameters, "name", "value");
                    if (settings is null) throw new DiscoveryException("SETTINGS_UNAVAILABLE", "Settings are unavailable.");
                    var settingName = RequiredString(parameters, "name");
                    if (!parameters.TryGetProperty("value", out var settingValue) || settingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new DiscoveryException("INVALID_REQUEST", "A setting value must be true or false.");
                    result = SettingsWire.Snapshot(await settings.SetAsync(settingName, settingValue.GetBoolean(), request.Cancellation.Token));
                    break;
                case "settings.listfile.read":
                case "settings.listfile.check":
                    RequireOnly(parameters);
                    if (settings is null || listfile is null) throw new DiscoveryException("LISTFILE_UNAVAILABLE", "Community file names are unavailable in this engine.");
                    result = SettingsWire.Listfile(method == "settings.listfile.read"
                        ? await listfile.StatusAsync(request.Cancellation.Token) : await listfile.CheckAsync(request.Cancellation.Token));
                    break;
                case "settings.storage.measure":
                    RequireOnly(parameters);
                    if (settings is null) throw new DiscoveryException("SETTINGS_UNAVAILABLE", "Settings are unavailable.");
                    result = SettingsWire.Measurement(await settings.MeasureAsync(request.Cancellation.Token));
                    break;
                case "settings.storage.builds":
                    RequireOnly(parameters);
                    if (preparedData is null) throw new DiscoveryException("PREPARED_DATA_UNAVAILABLE", "Prepared data per build is unavailable.");
                    result = await preparedData.ListAsync(request.Cancellation.Token);
                    break;
                case "settings.storage.free":
                    RequireOnly(parameters, "productId", "buildIdentity");
                    if (preparedData is null) throw new DiscoveryException("PREPARED_DATA_UNAVAILABLE", "Prepared data per build is unavailable.");
                    result = await preparedData.FreeAsync(new(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity")), request.Cancellation.Token);
                    break;
                case "workspace.open":
                    RequireOnly(parameters, "productId", "buildIdentity", "sourceRegion");
                    if (workspace is null) throw new DiscoveryException("OPEN_UNAVAILABLE", "Configuration preparation is unavailable.");
                    var opening = new WorkspaceRequest(RequiredString(parameters, "productId"), RequiredString(parameters, "buildIdentity"), RequiredString(parameters, "sourceRegion"));
                    long sequence = 0;
                    result = await workspace.OpenWithProgressAsync(opening, progress => WriteAsync(output, new { protocolVersion = 1, type = "progress", requestId = id,
                        sequence = Interlocked.Increment(ref sequence), stage = progress.Stage, step = progress.Step, region = opening.SourceRegion, completed = (int?)null, total = (int?)null, unit = (string?)null }), request.Cancellation.Token);
                    break;
                default: throw new DiscoveryException("UNKNOWN_METHOD", "The requested operation is not available.");
            }
            await ReplyAsync(output, id, result, maximumBytes, frameKind);
            pendingGeometryLease = null; // Opaque ownership transfers only after actual terminal Flush.
        }
        catch (OperationCanceledException) { await TryErrorAsync(output, id, "CANCELED", method == "workspace.open" ? "Opening canceled." : method == "discovery.refreshAll" ? "Discovery canceled. Products checked before stopping keep their new results." : method == "tables.prepare" ? "Table loading canceled." : method == "download.check" ? "Check stopped. Nothing new was recorded; an earlier check stays as it was." : "Operation canceled.", true, maximumBytes, frameKind); }
        catch (DiscoveryException ex) { await TryErrorAsync(output, id, ex.Code, ex.Message, ex.Retryable, maximumBytes, frameKind, ex.Definition); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Engine request failed ({ex.GetType().Name}).");
            await TryErrorAsync(output, id, "ENGINE_FAILURE", "The engine could not complete this operation.", true, maximumBytes, frameKind);
        }
        finally
        {
            try
            {
                if (pendingGeometryLease is not null)
                {
                    try { await geometryLeases!.CloseAsync(pendingGeometryLease); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        Interlocked.CompareExchange(ref geometryCleanupFailure,
                            new DiscoveryException("CREATURE_GEOMETRY_CLEANUP_FAILED", "Geometry resources did not retire safely."), null);
                    }
                }
                if (effectsLease is not null)
                {
                    try { await effectsLease.DisposeAsync(); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        // Observe actual service debt without an unobserved finally fault or
                        // another terminal frame. Service retains the failed capacity until shutdown.
                        Interlocked.CompareExchange(ref effectsCleanupFailure,
                            new DiscoveryException("ITEM_EFFECTS_CLEANUP_FAILED", "Effects resources did not retire safely."), null);
                    }
                }
            }
            finally
            {
                if (classificationPublicationHeld) classificationPublication.Release();
                if (creaturesPublicationHeld) creaturesPublication.Release();
                if (creaturesPreparationHeld) creaturesPreparation.Release();
                active.TryRemove(id, out _);
                request.Dispose();
                request.Retired.TrySetResult();
            }
        }
    }
    private async Task<TableDataSnapshot> PrepareTableAsync(TableDataRequest request, Func<string, Task> progress,
        CancellationToken token)
    {
        var preparation = tableData ?? throw new DiscoveryException("TABLE_DATA_UNAVAILABLE", "Table data loading is unavailable.");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await preparation.PrepareAsync(request, progress, token); }
            catch (DiscoveryException failure) when (failure.Code == "TABLE_DATA_BUSY")
            {
                if (attempt == 7) break;
                await progress("Waiting for shared table preparation");
                // Availability observes actual producer retirement without becoming its consumer:
                // canceling this request cannot stop the unrelated active table preparation.
                await preparation.WaitForAvailabilityAsync(token);
            }
        }
        throw new DiscoveryException("TABLE_DATA_BUSY", "Table preparation remains busy after bounded ownership attempts. Retry the requested table.", true);
    }
    private static string LimitCode(FrameKind kind) => kind switch
    { FrameKind.CreatureGeometry => "CREATURE_GEOMETRY_RESOURCE_LIMIT", FrameKind.Creatures => "CREATURES_RESULT_LIMIT", FrameKind.ItemEffects => "ITEM_EFFECTS_RESULT_LIMIT", _ => "ITEM_CLASSIFICATIONS_RESULT_LIMIT" };
    private static DiscoveryException CreaturesQueryBusy() => new("CREATURES_QUERY_BUSY", "Creature results are at their bounded publication capacity.", true);
    private async Task TryErrorAsync(Stream output, string id, string code, string message, bool retryable, int maximumBytes = MaxFrameBytes, FrameKind frameKind = FrameKind.General,
        TableDefinitionProblem? definition = null)
    {
        try
        {
            try { await ErrorAsync(output, id, code, message, retryable, maximumBytes, frameKind, definition); }
            catch (DiscoveryException ex) when (ex.Code == LimitCode(frameKind) && frameKind != FrameKind.General)
            {
                await ErrorAsync(output, id, ex.Code, "The complete result exceeds its bounded frame limit.", false, maximumBytes, frameKind);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private Task ReplyAsync(Stream output, string id, object result, int maximumBytes = MaxFrameBytes, FrameKind frameKind = FrameKind.General) =>
        WriteAsync(output, new { protocolVersion = 1, id, result }, maximumBytes, frameKind);
    private Task ErrorAsync(Stream output, string? id, string code, string message, bool retryable = false, int maximumBytes = MaxFrameBytes, FrameKind frameKind = FrameKind.General,
        TableDefinitionProblem? definition = null) =>
        WriteAsync(output, new { protocolVersion = 1, id, error = ErrorBody(code, message, retryable, definition) }, maximumBytes, frameKind);
    /// <summary>An EngineError. <c>definition</c> is present only when no definition can read a table (D101).</summary>
    internal static object ErrorBody(string code, string message, bool retryable, TableDefinitionProblem? definition = null) => definition is null
        ? new { code, message, retryable }
        : new { code, message, retryable, definition = new { tableId = definition.TableId, definitionFetch = definition.DefinitionFetch } };
    internal static object ErrorBody(DiscoveryException error) => ErrorBody(error.Code, error.Message, error.Retryable, error.Definition);
    private async Task WriteAsync(Stream output, object value, int maximumBytes = MaxFrameBytes, FrameKind frameKind = FrameKind.General)
    {
        if (frameKind != FrameKind.General)
        {
            // Serialize into a fixed-capacity sink before touching stdout. Escaping may expand
            // fifty pairs of finite source labels beyond the frame budget; rejected output
            // must never allocate its complete encoded size or emit a partial success frame.
            // Charge encoding through actual output retirement. Completed application
            // consumers can otherwise leave up to32 encoded frames waiting on stdout.
            var encoding = frameKind switch { FrameKind.Creatures or FrameKind.CreatureGeometry => creaturesEncoding, FrameKind.ItemEffects => effectsEncoding, _ => classificationEncoding };
            await encoding.WaitAsync();
            try
            {
                using var frame = new ClassificationFrameBuffer(maximumBytes - 1, LimitCode(frameKind));
                await JsonSerializer.SerializeAsync(frame, value, Json);
                await outputLock.WaitAsync();
                try { await output.WriteAsync(frame.Written); await output.WriteAsync("\n"u8.ToArray()); await output.FlushAsync(); }
                finally { outputLock.Release(); }
            }
            finally { encoding.Release(); }
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximumBytes) throw new DiscoveryException("RESULT_LIMIT", "The result exceeds the protocol frame limit.");
        await outputLock.WaitAsync();
        try { await output.WriteAsync(bytes); await output.WriteAsync("\n"u8.ToArray()); await output.FlushAsync(); }
        finally { outputLock.Release(); }
    }
    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() > 1))
            throw new DiscoveryException("INVALID_REQUEST", "A JSON object without duplicate fields is required.");
    }
    private static readonly string[] InventoryContextNames = ["productId", "buildIdentity", "sourceRegion", "generation", "limit", "after"];
    private static readonly string[] InventoryFilterNames = ["fileDataId", "contentKey", "localeFlags", "contentFlags", "family", "storedOnly"];
    private static void RequireOnly(JsonElement value, params string[] names)
    {
        if (value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw new DiscoveryException("INVALID_REQUEST", "The request has unsupported parameters.");
    }
    private static string RequiredString(JsonElement value, string name) => OptionalString(value, name)
        ?? throw new DiscoveryException("INVALID_REQUEST", $"The {name} parameter is required.");
    private static ItemsContext ItemsContextOf(JsonElement value) => new(RequiredString(value, "productId"),
        RequiredString(value, "buildIdentity"), RequiredString(value, "sourceRegion"), RequiredString(value, "researchLocale"));
    private static CreaturesContext CreaturesContextOf(JsonElement value) => new(RequiredString(value, "productId"),
        RequiredString(value, "buildIdentity"), RequiredString(value, "sourceRegion"), RequiredString(value, "researchLocale"));
    private static string RequestString(JsonElement value)
    {
        try { return value.GetString()!; }
        catch (InvalidOperationException)
        { throw new DiscoveryException("INVALID_REQUEST", "Request text must contain valid Unicode."); }
    }
    private static string? OptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String || RequestString(property) is not { Length: > 0 and <= 2048 } text)
            throw new DiscoveryException("INVALID_REQUEST", $"The {name} parameter must be a bounded string.");
        return text;
    }
}
