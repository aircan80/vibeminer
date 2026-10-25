using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task RealConfigurationsPublishOneCasAndAtomicAdmissionAcknowledgementWithoutResolverDownloads()
    {
        await using var fixture = await OpeningFixture.CreateAsync(duplicateRows: true);
        var stages = new List<string>();
        var workspace = await fixture.Service.OpenAsync(fixture.Request, stage => { stages.Add(stage); return Task.CompletedTask; });
        Assert.Equal("home-configurations-v1", workspace.PreparationProfile);
        Assert.Equal("configurationReady", workspace.Readiness); Assert.Equal("configurationsVerified", workspace.MetadataState);
        Assert.Equal("notPrepared", workspace.InventoryState); Assert.Equal("unknown", workspace.RetentionState);
        Assert.Equal("onDemand", workspace.DownloadPolicy); Assert.Null(workspace.ResearchLocale); Assert.Null(workspace.PlatformScope);
        Assert.False(workspace.Build.IsNew); Assert.False(workspace.Build.IsLocal); Assert.Equal("unknown", workspace.Build.Availability);
        Assert.Equal(fixture.Request.BuildIdentity, workspace.Build.Id);
        Assert.Equal("us", workspace.SourceReference.Region); Assert.Equal(0, workspace.SourceReference.VersionsRowOrdinal);
        Assert.Equal(2, workspace.Configurations.Count); Assert.Equal(2, fixture.Calls.Count);
        Assert.All(fixture.Calls, url => Assert.Contains("/config/", url, StringComparison.Ordinal));
        Assert.Equal(new[] { "buildConfig", "cdnConfig" }, workspace.Configurations.Select(receipt => receipt.Role));
        Assert.All(workspace.Configurations, receipt => Assert.True(receipt.ParsedFieldCount > 0));
        Assert.Contains("Finalizing workspace", stages);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM open_acknowledgements"));
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM configuration_objects"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Directory.Path, "staging-v1")));
    }
    [Fact]
    public async Task HealthyReopenAfterRestartUsesNoHttpAndKeepsOriginalReceipts()
    {
        using var directory = new TempDirectory();
        ConfigurationReceipt[] original;
        string identity;
        await using (var fixture = await OpeningFixture.CreateAsync(directory: directory))
        {
            var first = await fixture.Service.OpenAsync(fixture.Request); original = first.Configurations.ToArray(); identity = fixture.Request.BuildIdentity;
        }
        await using (var fixture = await OpeningFixture.CreateAsync(directory: directory, seed: false))
        {
            fixture.Response = _ => throw new InvalidOperationException("Reopening must be local only.");
            fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
            var second = await fixture.Service.OpenAsync(new("wow", identity, "us"));
            Assert.Empty(fixture.Calls);
            foreach (var receipt in second.Configurations)
            {
                var before = original.Single(value => value.Role == receipt.Role);
                Assert.Equal(before.Sha256, receipt.Sha256); Assert.Equal(before.SourceUrl, receipt.SourceUrl);
                Assert.Equal(before.RetrievedAt, receipt.RetrievedAt); Assert.NotEqual(before.VerifiedAt, receipt.VerifiedAt);
            }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptAdmittedBytesFailWithoutImplicitHttp(bool corrupt)
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var first = await fixture.Service.OpenAsync(fixture.Request);
        var stored = first.Configurations[0];
        var path = Path.Combine(fixture.Directory.Path, "objects-v1", stored.Sha256[..2], stored.Sha256[2..]);
        if (corrupt) await File.WriteAllBytesAsync(path, [0]); else File.Delete(path);
        fixture.Response = _ => throw new InvalidOperationException("No repair may be implicit.");
        var before = fixture.Calls.Count;
        var exception = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request));
        Assert.Equal(corrupt ? "LOCAL_CONFIGURATION_CORRUPT" : "LOCAL_CONFIGURATION_MISSING", exception.Code);
        Assert.Equal(before, fixture.Calls.Count); Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
    }
    [Fact]
    public async Task FailedInitialRetryReusesFirstVerifiedObjectAndOriginalReceipt()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var fail = true;
        fixture.Response = key => key == fixture.CdnKey && fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : fixture.GoodResponse(key);
        Assert.Equal("CONFIG_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM open_acknowledgements"));
        var saved = await fixture.Catalog.FindConfigurationAsync(fixture.BuildKey, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True((await fixture.Catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None)).Builds.Single(build => build.Id == fixture.Request.BuildIdentity).IsNew);
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1); fail = false;
        var result = await fixture.Service.OpenAsync(fixture.Request);
        Assert.Equal(3, fixture.Calls.Count);
        Assert.Equal(saved.RetrievedAt, result.Configurations.Single(receipt => receipt.Role == "buildConfig").RetrievedAt);
        Assert.False(result.Build.IsNew);
    }
    [Fact]
    public async Task WrongRawMd5NeverPublishesOrAcknowledges()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        fixture.Response = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("different = bytes\n") };
        Assert.Equal("CONFIG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM configuration_objects"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM open_acknowledgements"));
    }
    [Fact]
    public async Task CorrectHashWithMalformedGrammarNeverAdmitsHome()
    {
        await using var fixture = await OpeningFixture.CreateAsync(buildText: "duplicate = one\nduplicate = two\n");
        Assert.Equal("CONFIG_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM configuration_objects"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
    }
    [Theory]
    [InlineData("https://cdn.invalid/")]
    [InlineData("http://us.cdn.blizzard.com/")]
    [InlineData("https://us.cdn.blizzard.com.attacker.invalid/")]
    [InlineData("https://us.cdn.blizzard.com/other-path")]
    public async Task UnsupportedCapturedRoutesNeverCauseHttp(string server)
    {
        await using var fixture = await OpeningFixture.CreateAsync(server: server);
        Assert.Equal("SOURCE_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        Assert.Empty(fixture.Calls);
    }
    [Theory]
    [InlineData("versions", "text-metadata-v99")]
    [InlineData("cdns", "text-metadata-v99")]
    [InlineData("versions", "")]
    [InlineData("cdns", "")]
    public async Task UnsupportedOrCorruptSavedParserRevisionCannotAuthorizeAcquisition(string role, string revision)
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        using (var database = new SqliteConnection($"Data Source={Path.Combine(fixture.Directory.Path, "discovery-v1.sqlite3")};Pooling=False"))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE documents SET parser_revision=$revision WHERE role=$role";
            command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$role", role);
            command.ExecuteNonQuery();
        }
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request));
        Assert.Equal("SOURCE_UNSUPPORTED", error.Code);
        Assert.Empty(fixture.Calls);
        Assert.False(Directory.Exists(Path.Combine(fixture.Directory.Path, "objects-v1")));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM configuration_objects"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM open_acknowledgements"));
    }
    [Theory]
    [InlineData("binary/octet-stream")]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain")]
    public async Task SupportedConfigurationMediaTypesStillRequireCompleteHashAndGrammarVerification(string mediaType)
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        fixture.Response = key =>
        {
            var response = fixture.GoodResponse(key);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return response;
        };
        var result = await fixture.Service.OpenAsync(fixture.Request);
        Assert.Equal("configurationReady", result.Readiness);
        Assert.Equal(2, result.Configurations.Count); Assert.Equal(2, fixture.Calls.Count);
    }
    [Fact]
    public async Task HtmlConfigurationResponseCannotPublishEvenWhenItsBodyMatchesTheAdvertisedHash()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        fixture.Response = key =>
        {
            var response = fixture.GoodResponse(key);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return response;
        };
        Assert.Equal("CONFIG_CONTENT_TYPE", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM configuration_objects"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM open_acknowledgements"));
    }
    [Fact]
    public async Task RedirectAndStreamingLimitsFailSafely()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        fixture.Response = _ => new HttpResponseMessage(HttpStatusCode.Redirect);
        Assert.Equal("SOURCE_REDIRECT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        fixture.Response = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[1024 * 1024 + 1])) };
        Assert.Equal("CONFIG_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(fixture.Request))).Code);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
    }
    [Fact]
    public async Task SamePhysicalConfigurationsAreReusedAcrossExactBuildIdentities()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        await fixture.Service.OpenAsync(fixture.Request);
        var other = (await fixture.Catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None)).Builds.Single(build => build.Id != fixture.Request.BuildIdentity);
        var workspace = await fixture.Service.OpenAsync(new("wow", other.Id, "us"));
        Assert.Equal(2, fixture.Calls.Count); Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM configuration_objects"));
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(other.Id, workspace.Build.Id);
    }
    [Fact]
    public async Task CatalogVersionTwoUpgradesToWorkspaceSchema()
    {
        using var directory = new TempDirectory();
        await using (var fixture = await OpeningFixture.CreateAsync(directory: directory)) { }
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = CatalogMigrationFixture.RemoveV9 + "DROP TABLE encoded_objects; DROP TABLE content_locators; DROP TABLE table_admissions; DROP TABLE table_artifacts; DROP TABLE content_objects; DROP TABLE resolver_artifacts; DROP TABLE download_admissions; DROP TABLE download_artifact_sources; DROP TABLE download_artifacts; DROP TABLE inventory_checkpoints; DROP TABLE inventory_merge_results; DROP TABLE inventory_merge_state; DROP TABLE inventory_resolution_cursor; DROP TABLE inventory_available; DROP TABLE inventory_units; DROP TABLE inventory_logical; DROP TABLE inventory_tags; DROP TABLE inventory_origins; DROP TABLE en_objects; DROP TABLE en_candidates; DROP TABLE en_content; DROP TABLE inventory_generations; DROP TABLE metadata_objects; DROP TABLE open_acknowledgements; DROP TABLE workspace_configurations; DROP TABLE workspace_admissions; DROP TABLE configuration_objects; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        await using var upgraded = await OpeningFixture.CreateAsync(directory: directory, seed: false);
        var page = await upgraded.Catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None);
        var request = new WorkspaceRequest("wow", page.Builds[0].Id, "us");
        Assert.Equal("configurationReady", (await upgraded.Service.OpenAsync(request)).Readiness);
        Assert.Equal(21L, upgraded.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task CancellationWaitsForConfigurationProducerCleanupAndDoesNotAcknowledge()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AsyncResponse = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopping.TrySetResult(); await cleanup.Task; }
            throw new InvalidOperationException();
        };
        using var cancel = new CancellationTokenSource();
        var opening = fixture.Service.OpenAsync(fixture.Request, cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(opening.IsCompleted);
        cleanup.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM open_acknowledgements"));
    }
    [Fact]
    public async Task SharedConsumersKeepOneProducerWhileAnotherContextIsBusyAndObserverFailureIsIsolated()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AsyncResponse = async (request, token) =>
        { started.TrySetResult(); await release.Task.WaitAsync(token); return fixture.GoodResponse(request.RequestUri!.Segments[^1]); };
        using var cancel = new CancellationTokenSource();
        var first = fixture.Service.OpenAsync(fixture.Request, cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Service.OpenAsync(fixture.Request, _ => throw new IOException("Detached observer"));
        var another = fixture.Request with { SourceRegion = "eu" };
        Assert.Equal("OPEN_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Service.OpenAsync(another))).Code);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted); release.SetResult();
        var result = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("configurationReady", result.Readiness); Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
    }
    [Fact]
    public async Task RefreshDuringOpeningDoesNotReplacePinnedSourceReceiptsAfterAdmission()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var original = await fixture.Catalog.ResolveOpeningAsync(fixture.Request, CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AsyncResponse = async (request, token) =>
        { started.TrySetResult(); await release.Task.WaitAsync(token); return fixture.GoodResponse(request.RequestUri!.Segments[^1]); };
        var opening = fixture.Service.OpenAsync(fixture.Request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var versions = Fixtures.Versions($"eu|{fixture.BuildKey}|{fixture.CdnKey}||00123|1.2.3.00123||selected", $"us|{fixture.BuildKey}|{fixture.CdnKey}||00123|1.2.3.00123||selected");
        var cdns = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0\neu|tpr/wow|us.cdn.blizzard.com|https://us.cdn.blizzard.com/|tpr/configs/data\nus|tpr/wow|us.cdn.blizzard.com|https://us.cdn.blizzard.com/|tpr/configs/data\n";
        var attempt = await Fixtures.Attempt(versions, cdns);
        var generation = await fixture.Catalog.BeginRefreshAsync("wow", CancellationToken.None);
        await fixture.Catalog.CommitAsync("wow", generation, attempt, fixture.Clock.Now.ToString("O"), CancellationToken.None);
        release.SetResult(); var result = await opening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original.Reference, result.SourceReference);
        Assert.Contains(result.Build.SourceReceipts, receipt => receipt.Region == "us" && receipt.Role == "versions" && receipt.Sha256 == result.SourceReference.VersionsSha256 && receipt.RowOrdinal == result.SourceReference.VersionsRowOrdinal);
        Assert.Contains(result.Build.SourceReceipts, receipt => receipt.Region == "us" && receipt.Role == "cdns" && receipt.Sha256 == result.SourceReference.CdnsSha256 && receipt.RowOrdinal == result.SourceReference.CdnsRowOrdinal);
        Assert.NotEqual(original.Reference.VersionsSha256, (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, CancellationToken.None)).Reference.VersionsSha256);
        Assert.False(result.Build.IsNew);
    }
    [Fact]
    public async Task DurableWorkspaceCommitWinsLateCancellation()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await using var service = new WorkspaceService(new DelegatePreparer(async (request, progress, token) =>
        { var result = await fixture.Preparer.PrepareAsync(request, progress, token); cancellation.Cancel(); return result; }));
        var result = await service.OpenAsync(fixture.Request, cancellationToken: cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested); Assert.False(result.Build.IsNew);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
    }
    [Fact]
    public async Task CanceledSharedConsumerDrainsItsProgressBeforeTerminalAndReceivesNoLaterProgress()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var saved = await fixture.Service.OpenAsync(fixture.Request);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WorkspaceService(new DelegatePreparer(async (_, progress, _) =>
        { await progress("first"); await progress("later"); return saved; }));
        using var cancellation = new CancellationTokenSource(); var callbacks = 0;
        var first = service.OpenAsync(fixture.Request, async _ =>
        { Interlocked.Increment(ref callbacks); callbackStarted.TrySetResult(); await releaseCallback.Task; }, cancellation.Token);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.OpenAsync(fixture.Request);
        cancellation.Cancel(); Assert.False(first.IsCompleted);
        releaseCallback.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(saved, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, callbacks);
    }
    [Fact]
    public async Task StructuredOpeningPreservesStepAndLegacyOpeningProjectsStage()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var saved = await fixture.Service.OpenAsync(fixture.Request);
        var expected = new WorkspacePreparationProgress("Resolving root inventory", new(18, 22, "Connect file references to encoded objects."));
        await using var service = new WorkspaceService(new StructuredPreparer(saved, expected));
        WorkspacePreparationProgress? received = null;
        Assert.Equal(saved, await service.OpenWithProgressAsync(fixture.Request, value => { received = value; return Task.CompletedTask; }));
        Assert.Equal(expected, received);
        string? legacy = null;
        Assert.Equal(saved, await service.OpenAsync(fixture.Request, value => { legacy = value; return Task.CompletedTask; }));
        Assert.Equal(expected.Stage, legacy);
    }
    [Fact]
    public async Task LegacyPreparerAdaptsWithoutInventingStepNumbers()
    {
        await using var fixture = await OpeningFixture.CreateAsync();
        var saved = await fixture.Service.OpenAsync(fixture.Request);
        await using var service = new WorkspaceService(new DelegatePreparer(async (_, progress, _) => { await progress("Checking configurations"); return saved; }));
        WorkspacePreparationProgress? received = null;
        await service.OpenWithProgressAsync(fixture.Request, value => { received = value; return Task.CompletedTask; });
        Assert.Equal(new WorkspacePreparationProgress("Checking configurations"), received);
    }
    [Fact]
    public async Task RealHostProcessOpensDownloadScopeFromRetainedMetadataWithoutFullCatalogGate()
    {
        using var directory = new TempDirectory(); WorkspaceRequest request;
        var authored = InventoryFixture.Create(); request = new("wow", authored.BuildIdentity, "us");
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        await using (var objects = new VerifiedObjectStore(directory.Path))
        using (var client = new HttpClient(new Handler((message, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = message, Content = new ByteArrayContent(authored.Responses[message.RequestUri!.AbsoluteUri]) }))))
        using (var preparer = new InventoryWorkspacePreparer(catalog, objects, client))
        {
            var generation = await catalog.BeginRefreshAsync("wow", default);
            await catalog.CommitAsync("wow", generation, authored.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            await preparer.PrepareAsync(request, _ => Task.CompletedTask, default);
        }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = dotnetRoot is null ? "dotnet" : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(VibeMiner.Host.ProtocolHost).Assembly.Location);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(directory.Path);
        // A regression attempting HTTP cannot reach an external source in this deterministic check.
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1"; start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1"; start.Environment["NO_PROXY"] = "";
        using var process = System.Diagnostics.Process.Start(start)!;
        var diagnostics = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, id = "open", method = "workspace.open", @params = new { productId = request.ProductId, buildIdentity = request.BuildIdentity, sourceRegion = request.SourceRegion } }));
            await process.StandardInput.FlushAsync(); long previousSequence = 0; var previousStep = 0; var finalStep = 0; System.Text.Json.JsonElement result;
            while (true)
            {
                using var frame = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
                if (frame.RootElement.TryGetProperty("type", out _))
                {
                    var sequence = frame.RootElement.GetProperty("sequence").GetInt64(); Assert.True(sequence > previousSequence); previousSequence = sequence;
                    var step = frame.RootElement.GetProperty("step");
                    var index = step.GetProperty("index").GetInt32(); Assert.True(index >= previousStep); previousStep = index;
                    Assert.InRange(step.GetProperty("description").GetString()!.Length, 1, 400);
                    if (step.GetProperty("total").ValueKind == System.Text.Json.JsonValueKind.Number)
                    { finalStep = step.GetProperty("total").GetInt32(); Assert.InRange(index, 1, finalStep); }
                    continue;
                }
                Assert.False(frame.RootElement.TryGetProperty("error", out _), frame.RootElement.ToString());
                result = frame.RootElement.GetProperty("result").Clone(); break;
            }
            Assert.Equal("partial", result.GetProperty("readiness").GetString());
            Assert.Equal("verified", result.GetProperty("metadataState").GetString());
            Assert.Equal("notPrepared", result.GetProperty("inventoryState").GetString());
            Assert.Equal("home-download-v1", result.GetProperty("preparationProfile").GetString());
            Assert.Equal(5, finalStep); Assert.Equal(finalStep, previousStep);
            Assert.Equal(2, result.GetProperty("configurations").GetArrayLength());
            var reference = result.GetProperty("sourceReference");
            foreach (var role in new[] { "versions", "cdns" })
            {
                var sha = reference.GetProperty(role + "Sha256").GetString();
                var ordinal = reference.GetProperty(role == "versions" ? "versionsRowOrdinal" : "cdnsRowOrdinal").GetInt32();
                Assert.Contains(result.GetProperty("build").GetProperty("sourceReceipts").EnumerateArray(), receipt =>
                    receipt.GetProperty("region").GetString() == request.SourceRegion && receipt.GetProperty("role").GetString() == role &&
                    receipt.GetProperty("sha256").GetString() == sha && receipt.GetProperty("rowOrdinal").GetInt32() == ordinal);
            }
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, id = "files", method = "download.read", @params = new { productId = request.ProductId, buildIdentity = request.BuildIdentity, sourceRegion = request.SourceRegion, ledgerId = result.GetProperty("download").GetProperty("ledgerId").GetString(), limit = 3 } }));
            await process.StandardInput.FlushAsync();
            using var page = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            Assert.Equal(2, page.RootElement.GetProperty("result").GetProperty("entries").GetArrayLength());
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, id = "tables", method = "tables.catalog", @params = new { productId = request.ProductId, buildIdentity = request.BuildIdentity, sourceRegion = request.SourceRegion, query = "ItemClass", limit = 5 } }));
            await process.StandardInput.FlushAsync();
            using var tables = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            var entries = tables.RootElement.GetProperty("result").GetProperty("entries");
            Assert.Contains(entries.EnumerateArray(), entry => entry.GetProperty("name").GetString() == "ItemClass" && entry.GetProperty("rowsState").GetString() == "notPrepared");
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, id = "all-tables", method = "tables.catalog", @params = new { productId = request.ProductId, buildIdentity = request.BuildIdentity, sourceRegion = request.SourceRegion, query = "", limit = 5 } }));
            await process.StandardInput.FlushAsync();
            using var allTables = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            Assert.Equal("", allTables.RootElement.GetProperty("result").GetProperty("query").GetString());
            Assert.Equal("1346", allTables.RootElement.GetProperty("result").GetProperty("totalCount").GetString());
            Assert.Equal(5, allTables.RootElement.GetProperty("result").GetProperty("entries").GetArrayLength());
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, id = "definition", method = "tables.definition", @params = new { productId = request.ProductId, buildIdentity = request.BuildIdentity, sourceRegion = request.SourceRegion, tableId = "50238ec2" } }));
            await process.StandardInput.FlushAsync();
            using var definition = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            var item = definition.RootElement.GetProperty("result");
            Assert.Equal("Item", item.GetProperty("entry").GetProperty("name").GetString());
            Assert.Equal("notChecked", item.GetProperty("entry").GetProperty("sourceState").GetString());
            // D080: the unlisted 1.2.3.12345 build previews the layouts a 1.x file could open by.
            Assert.NotEmpty(item.GetProperty("definition").GetProperty("candidates").EnumerateArray());
            Assert.Equal("SCHEMA_BUILD_NOT_LISTED", item.GetProperty("problem").GetProperty("code").GetString());
            var classField = Assert.Single(item.GetProperty("definition").GetProperty("columns").EnumerateArray(), field => field.GetProperty("name").GetString() == "ClassID");
            Assert.Equal("ItemClass", classField.GetProperty("reference").GetProperty("table").GetString());
            Assert.Equal("ClassID", classField.GetProperty("reference").GetProperty("column").GetString());
            await process.StandardInput.WriteLineAsync("{\"protocolVersion\":1,\"id\":\"stop\",\"method\":\"shutdown\",\"params\":{}}");
            await process.StandardInput.FlushAsync(); await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await diagnostics);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private sealed class DelegatePreparer(Func<WorkspaceRequest, Func<string, Task>, CancellationToken, Task<WorkspaceSnapshot>> run) : IWorkspacePreparer
    { public Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token) => run(request, progress, token); }
    private sealed class StructuredPreparer(WorkspaceSnapshot snapshot, WorkspacePreparationProgress update) : IWorkspacePreparer
    {
        public Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token) =>
            throw new InvalidOperationException("Structured opening must use the structured preparer port.");
        public async Task<WorkspaceSnapshot> PrepareWithProgressAsync(WorkspaceRequest request, Func<WorkspacePreparationProgress, Task> progress, CancellationToken token)
        { await progress(update); return snapshot; }
    }
    internal sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class OpeningFixture : IAsyncDisposable
    {
        public TempDirectory Directory { get; private init; } = null!;
        private bool ownDirectory;
        public DiscoveryCatalog Catalog { get; private init; } = null!;
        public VerifiedObjectStore Objects { get; private init; } = null!;
        public ConfigurationWorkspacePreparer Preparer { get; private set; } = null!;
        public WorkspaceService Service { get; private set; } = null!;
        private HttpClient client = null!;
        public WorkspaceRequest Request { get; private set; } = null!;
        public TestClock Clock { get; } = new();
        public List<string> Calls { get; } = [];
        public string BuildKey { get; private set; } = "";
        public string CdnKey { get; private set; } = "";
        /// <summary>A republished CDN configuration for the same build, listing one more archive.</summary>
        public string RepublishedCdnKey { get; private set; } = "";
        private byte[] buildBytes = [];
        private byte[] cdnBytes = [];
        private byte[] republishedCdnBytes = [];
        private string cdnsDocument = "";
        public Func<string, HttpResponseMessage>? Response;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponse;
        public HttpResponseMessage GoodResponse(string key) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(key == BuildKey ? buildBytes : key == CdnKey ? cdnBytes : key == RepublishedCdnKey ? republishedCdnBytes : throw new InvalidOperationException("Unexpected configuration key.")) };
        /// <summary>Commit a refresh that advertises the selected build with another CDN configuration.</summary>
        public Task<DiscoveryPage> RepublishAsync(string? cdnKey = null) =>
            CommitDiscoveryAsync(Fixtures.Versions($"us|{BuildKey}|{cdnKey ?? RepublishedCdnKey}||00123|1.2.3.00123||selected"));
        public async Task<DiscoveryPage> CommitDiscoveryAsync(string versions)
        {
            var attempt = await Fixtures.Attempt(versions, cdnsDocument);
            var generation = await Catalog.BeginRefreshAsync("wow", CancellationToken.None);
            return await Catalog.CommitAsync("wow", generation, attempt, Clock.Now.ToString("O"), CancellationToken.None);
        }
        public static async Task<OpeningFixture> CreateAsync(string? buildText = null, string server = "https://us.cdn.blizzard.com/?maxhosts=2", bool duplicateRows = false, TempDirectory? directory = null, bool seed = true)
        {
            var temporary = directory ?? new TempDirectory();
            var fixture = new OpeningFixture { Directory = temporary, ownDirectory = directory is null,
                Catalog = await DiscoveryCatalog.OpenAsync(temporary.Path), Objects = new VerifiedObjectStore(temporary.Path) };
            fixture.buildBytes = Encoding.UTF8.GetBytes(buildText ?? $"# authored configuration\nroot = {Fixtures.A}\nencoding = {Fixtures.B} {Fixtures.C}\nencoding-size = 100 90\nunknown-field = preserve both\nfeature-placeholder = true\n");
            fixture.cdnBytes = Encoding.UTF8.GetBytes("# authored CDN config\narchives = " + Fixtures.C + "\nunknown-field = retained\n");
            fixture.BuildKey = Convert.ToHexStringLower(MD5.HashData(fixture.buildBytes));
            fixture.CdnKey = Convert.ToHexStringLower(MD5.HashData(fixture.cdnBytes));
            fixture.republishedCdnBytes = Encoding.UTF8.GetBytes("# authored republished CDN config\narchives = " + Fixtures.C + " " + Fixtures.A + "\nunknown-field = retained\n");
            fixture.RepublishedCdnKey = Convert.ToHexStringLower(MD5.HashData(fixture.republishedCdnBytes));
            fixture.client = new HttpClient(new Handler(async (request, token) =>
            {
                var uri = request.RequestUri!.AbsoluteUri; fixture.Calls.Add(uri);
                if (!uri.StartsWith("https://us.cdn.blizzard.com/tpr/wow/config/", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected HTTP route: " + uri);
                if (fixture.AsyncResponse is not null) return await fixture.AsyncResponse(request, token);
                var key = request.RequestUri.Segments[^1]; return fixture.Response?.Invoke(key) ?? fixture.GoodResponse(key);
            }));
            fixture.cdnsDocument = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0\nus|tpr/wow|us.cdn.blizzard.com|" + server + "|tpr/configs/data\n";
            fixture.Preparer = new(fixture.Catalog, fixture.Objects, fixture.client, fixture.Clock);
            fixture.Service = new(fixture.Preparer);
            if (seed)
            {
                string Row(string unknown) => $"us|{fixture.BuildKey}|{fixture.CdnKey}||00123|1.2.3.00123||{unknown}";
                await fixture.CommitDiscoveryAsync(Fixtures.Versions(Row("baseline")));
                var page = await fixture.CommitDiscoveryAsync(Fixtures.Versions(duplicateRows ? [Row("selected"), Row("selected")] : [Row("selected")]));
                fixture.Request = new("wow", page.Builds.Single(build => build.IdentityFields["Opaque"] == "selected").Id, "us");
            }
            return fixture;
        }
        public long Scalar(string sql)
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(Directory.Path, "discovery-v1.sqlite3")};Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
        }
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); Preparer.Dispose(); client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); if (ownDirectory) Directory.Dispose(); }
    }
}
