using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using VibeMiner.Desktop.BackendFixture;

try
{
    if (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))
    {
        await Console.Error.WriteLineAsync("Usage: BackendFixture --data-dir <absolute authored-data directory>");
        return 2;
    }
    var directory = Path.GetFullPath(args[1]);
    await AuthoredScenario.EnsureOwnedDirectoryAsync(directory);
    var scenario = AuthoredScenario.Create();
    var controls = Path.Combine(directory, "authored-controls");
    Directory.CreateDirectory(controls);
    await File.WriteAllTextAsync(Path.Combine(controls, "scenario.json"), JsonSerializer.Serialize(new
    {
        authoredFixture = true,
        description = "Authored synthetic metadata/payloads; injected handler only; no external network.",
        scenario.Version, scenario.BuildIdentity, productId = "wow", sourceRegion = "us",
        gatedEncodingKey = scenario.GatedEncodingKey,
        objectCount = 2, totalEncodedBytes = scenario.Inventory.ExpectedEncodedBytes,
        releaseFile = Path.Combine(controls, "release"),
        gateStateFile = Path.Combine(controls, "gated-state.json"),
        attemptsFile = Path.Combine(controls, "gated-attempts.txt"),
        httpLogFile = Path.Combine(controls, "http.ndjson"),
        httpCountsFile = Path.Combine(controls, "http-counts.json")
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    using var handler = new AuthoredHttpHandler(scenario, controls);
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var resources = new AcquisitionResources();
    using var source = new OfficialDiscoverySource(client, resources: resources);
    await using var catalog = await DiscoveryCatalog.OpenAsync(directory);
    var discovery = new DiscoveryService(source, catalog);
    var history = await discovery.ReadAsync("wow");
    if (!history.HasSavedHistory)
    {
        var anyHistory = false;
        foreach (var product in ProductRegistry.Products.Where(product => product.Id != "wow"))
            if ((await discovery.ReadAsync(product.Id)).HasSavedHistory) { anyHistory = true; break; }
        if (!anyHistory) await discovery.RefreshAsync("wow", CancellationToken.None);
    }
    await using var objects = new VerifiedObjectStore(directory, catalog.RecordObjectInvalidityAsync);
    await using var sourcePlans = new EncodedSourcePlans(catalog, objects, resources);
    await using var acquisition = new AcquisitionCoordinator(new OfficialEncodedObjectProducer(catalog, objects, client, resources, sourcePlans));
    using var preparation = new DownloadWorkspacePreparer(catalog, objects, client, acquisition, resources);
    await using var workspace = new WorkspaceService(preparation);
    await using var tableStore = new TableDataStore(catalog, objects, client, acquisition, resources);
    await using var tables = new TableDataService(tableStore);
    var retentionDriver = new BuildRetentionDriver(catalog, objects, acquisition, sourcePlans, client, resources);
    var retention = new RetentionService(catalog, retentionDriver);
    await using var storedCoverage = new StoredCoverageWorker(catalog, objects, resources);
    var metadataPasses = new BackgroundMetadataGate();
    await using var rootScopes = new RootScopeWorker(catalog, objects, client, acquisition, resources, retentionDriver.NotifyIntentChanged, metadataPasses);
    await using var fileCatalogs = new FileCatalogWorker(catalog, objects, client, acquisition, resources, metadataPasses);
    try
    {
        await retention.RestoreAsync();
        storedCoverage.Start(); rootScopes.Start(); fileCatalogs.Start();
        var host = new ProtocolHost(discovery, workspace, new InventoryService(catalog),
            new DownloadService(new DownloadQuery(catalog, objects)), new TablesService(new TablesCatalog(catalog)), tables, retention,
            downloadSummaries: new DownloadSummaryService(catalog));
        await host.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    }
    finally { await retention.ShutdownAsync(); }
    return 0;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync($"Authored backend fixture failed ({exception.GetType().Name}): {exception.Message}");
    return 1;
}
