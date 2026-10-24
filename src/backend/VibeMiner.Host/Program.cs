using VibeMiner.Application;
using VibeMiner.Host;
using VibeMiner.Infrastructure;

try
{
    string dataDirectory;
    if (args.Length == 0)
        dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeMiner", "engine-v1");
    else if (args.Length == 2 && args[0] == "--data-dir" && Path.IsPathFullyQualified(args[1])) dataDirectory = args[1];
    else { await Console.Error.WriteLineAsync("Usage: VibeMiner.Host [--data-dir <absolute-path>]"); return 2; }
    using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = System.Net.DecompressionMethods.None };
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var resources = new AcquisitionResources();
    using var source = new OfficialDiscoverySource(client, resources: resources);
    await using var catalog = await DiscoveryCatalog.OpenAsync(dataDirectory);
    await using var objects = new VerifiedObjectStore(dataDirectory, catalog.RecordObjectInvalidityAsync);
    await using var sourcePlans = new EncodedSourcePlans(catalog, objects, resources);
    var encodedProducer = new OfficialEncodedObjectProducer(catalog, objects, client, resources, sourcePlans);
    await using var acquisition = new AcquisitionCoordinator(encodedProducer);
    using var preparation = new DownloadWorkspacePreparer(catalog, objects, client, acquisition, resources);
    await using var workspace = new WorkspaceService(preparation);
    await using var tableStore = new TableDataStore(catalog, objects, client, acquisition, resources);
    await using var tableData = new TableDataService(tableStore);
    await using var creaturesStore = new CreaturesStore(catalog, objects, tableData, resources);
    await using var creatures = new CreaturesService(creaturesStore);
    await using var creatureDisplayStore = new CreatureDisplayMetadataStore(catalog, objects, creaturesStore, tableData, resources);
    await using var creatureDisplays = new CreatureDisplayMetadataService(creatureDisplayStore);
    await using var itemsStore = new ItemsStore(catalog, objects, tableData, resources);
    await using var items = new ItemsService(itemsStore);
    await using var effectsStore = new ItemEffectsStore(catalog, objects, itemsStore, tableData, resources);
    await using var itemEffects = new ItemEffectsService(effectsStore);
    await using var classificationStore = new ItemClassificationsStore(catalog, objects, itemsStore, tableData, resources);
    await using var itemClassifications = new ItemClassificationsService(classificationStore);
    await using var assetSessions = new AssetResolutionSessions(catalog, objects, client, acquisition, resources);
    var iconArtifacts = new IconArtifacts(objects);
    await using var iconStore = new ItemIconsStore(catalog, objects, itemsStore, assetSessions, acquisition, resources, iconArtifacts);
    await using var itemIcons = new ItemIconsService(iconStore);
    await using var iconLeases = new ItemIconLeases(itemIcons);
    // Diagnostic source geometry (D052/D055): same shared asset sessions and acquisition pipeline as icons.
    await using var geometryStore = new CreatureGeometryStore(catalog, objects, creatureDisplayStore, assetSessions, acquisition, resources);
    await using var creatureGeometry = new CreatureGeometryService(geometryStore);
    await using var geometryLeases = new CreatureGeometryLeases(creatureGeometry);
    var retentionDriver = new BuildRetentionDriver(catalog, objects, acquisition, sourcePlans, client, resources);
    await using var storedCoverage = new StoredCoverageWorker(catalog, objects, resources);
    // Every file ROOT lists is measured after Open, in the background; Download all waits for it (D066).
    // Its build-sized pass never runs together with the file catalog's (D071).
    var metadataPasses = new BackgroundMetadataGate();
    await using var rootScopes = new RootScopeWorker(catalog, objects, client, acquisition, resources, retentionDriver.NotifyIntentChanged, metadataPasses);
    // The ROOT and install memberships Files lists are prepared after Open, in the background (D071).
    await using var fileCatalogs = new FileCatalogWorker(catalog, objects, client, acquisition, resources, metadataPasses);
    var retention = new RetentionService(catalog, retentionDriver);
    var settings = new SettingsService(new JsonSettingsStore(dataDirectory), new DataDirectoryMeasurer(dataDirectory),
        new VibeMiner.Domain.DownloadLimitSettings(resources.MaximumHttp, resources.MaximumBackgroundHttp, null, false));
    try
    {
        await retention.RestoreAsync();
        storedCoverage.Start(); rootScopes.Start(); fileCatalogs.Start();
        var host = new ProtocolHost(new DiscoveryService(source, catalog), workspace, new InventoryService(catalog),
            new DownloadService(new DownloadQuery(catalog, objects, resources)), new TablesService(new TablesCatalog(catalog)), tableData, retention, items, itemIcons, iconLeases, itemClassifications, creatures, creatureDisplays, itemEffects: itemEffects,
            creatureGeometry: creatureGeometry, geometryLeases: geometryLeases, settings: settings,
            downloadSummaries: new DownloadSummaryService(catalog),
            referenceText: new TableReferenceTextService(tableStore), follow: new TableFollowService(tableStore));
        await host.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    }
    finally { await retention.ShutdownAsync(); }
    return 0;
}
catch (VibeMiner.Domain.DiscoveryException exception) when (exception.Code is "CATALOG_RESET_REQUIRED" or "CATALOG_VERSION")
{
    // Typed catalog refusals carry fixed messages; the catalog file was not changed. The desktop maps the exit code.
    await Console.Error.WriteLineAsync($"Engine startup refused the catalog ({exception.Code}): {exception.Message}");
    return exception.Code == "CATALOG_RESET_REQUIRED" ? 3 : 4;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync($"Engine startup or transport failed ({exception.GetType().Name}).");
    return 1;
}
