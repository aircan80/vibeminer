using System.Net;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class PreparationProgressPlanTests
{
    [Theory]
    [InlineData(true, true, 22)]
    [InlineData(true, false, 18)]
    [InlineData(false, true, 18)]
    [InlineData(false, false, 14)]
    public async Task Fresh_plan_numbers_only_declared_work_and_preserves_actual_phase_order(bool download, bool size, int total)
    {
        var observed = new List<WorkspacePreparationProgress>();
        var plan = new PreparationProgressPlan(value => { observed.Add(value); return Task.CompletedTask; }, default);
        await plan.ReportAsync("Checking configuration");
        Assert.Equal(1, observed[0].Step!.Index);
        Assert.Null(observed[0].Step!.Total);
        plan.ConfigureFresh(download, size);
        foreach (var stage in plan.Stages) await plan.BeginAsync(stage, stage.ToString());
        Assert.Equal(Enumerable.Range(1, total), observed.Skip(1).Select(value => value.Step!.Index));
        Assert.All(observed.Skip(1), value =>
        {
            Assert.Equal(total, value.Step!.Total);
            Assert.InRange(value.Step.Description.Length, 1, 400);
        });
        Assert.Equal(download, plan.Stages.Contains(PreparationStage.DownloadAcquisition));
        Assert.Equal(download, plan.Stages.Contains(PreparationStage.DownloadSave));
        Assert.Equal(size, plan.Stages.Contains(PreparationStage.SizeAcquisition));
        Assert.Equal(size, plan.Stages.Contains(PreparationStage.SizeSave));
        Assert.Equal(PreparationStage.Seal, plan.Stages[^1]);
    }

    [Theory]
    [InlineData(true, true, 9)]
    [InlineData(true, false, 8)]
    [InlineData(false, true, 8)]
    [InlineData(false, false, 7)]
    public async Task Cached_plan_is_shorter_and_uses_canonical_role_order(bool download, bool size, int total)
    {
        var roles = new List<string> { "install", "root", "encoding" };
        if (size) roles.Insert(0, "size");
        if (download) roles.Insert(0, "download");
        var plan = new PreparationProgressPlan(_ => Task.CompletedTask, default);
        plan.ConfigureCached(roles);
        Assert.Equal(total, plan.Stages.Count);
        Assert.Equal(PreparationStage.Configurations, plan.Stages[0]);
        Assert.Equal(PreparationStage.CachedIndex, plan.Stages[1]);
        Assert.Equal(PreparationStage.EncodingRevalidation, plan.Stages[2]);
        Assert.Equal(PreparationStage.RootRevalidation, plan.Stages[3]);
        Assert.Equal(PreparationStage.InstallRevalidation, plan.Stages[4]);
        Assert.DoesNotContain(PreparationStage.Cleanup, plan.Stages);
        Assert.DoesNotContain(PreparationStage.CatalogDigest, plan.Stages);
        foreach (var stage in plan.Stages) await plan.BeginAsync(stage, "Rechecking");
        Assert.Equal(new PreparationStep(total, total,
            "Publish the complete file catalog and measured download totals, then open the build overview."), plan.CurrentStep);
    }

    [Fact]
    public async Task Descriptions_and_current_indices_do_not_depend_on_parsing_stage_text()
    {
        var events = new List<WorkspacePreparationProgress>();
        var plan = new PreparationProgressPlan(value => { events.Add(value); return Task.CompletedTask; }, default);
        plan.ConfigureFresh(true, true);
        await plan.BeginAsync(PreparationStage.RootResolution, "Assessing candidates");
        await plan.ReportAsync("Checking 12 objects");
        Assert.All(events, value => Assert.Equal(18, value.Step!.Index));
        Assert.Equal(events[0].Step!.Description, events[1].Step!.Description);
        plan.Select(PreparationStage.InstallResolution);
        await plan.ReportAsync("arbitrary per-stage counters · 1 of 3");
        Assert.Equal(19, events[^1].Step!.Index);
    }

    [Fact]
    public async Task Reporting_awaits_observers_and_observes_cancellation_and_observer_failure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new PreparationProgressPlan(async _ => { entered.SetResult(); await release.Task; }, default);
        var reporting = plan.ReportAsync("Checking configurations");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reporting.IsCompleted);
        release.SetResult();
        await reporting;
        using var cancellation = new CancellationTokenSource();
        plan = new(_ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.ReportAsync("Checking configurations"));
        var failure = new InvalidOperationException("authored observer failure");
        plan = new(_ => throw failure, default);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => plan.ReportAsync("Checking configurations")));
    }

    [Fact]
    public void Invalid_reconfiguration_backward_or_absent_phases_cannot_publish_false_numbers()
    {
        var plan = new PreparationProgressPlan(_ => Task.CompletedTask, default);
        Assert.Throws<InvalidOperationException>(() => plan.Select(PreparationStage.RootAcquisition));
        plan.ConfigureFresh(false, false);
        Assert.Throws<InvalidOperationException>(() => plan.ConfigureFresh(true, true));
        Assert.Throws<InvalidOperationException>(() => plan.Select(PreparationStage.DownloadResolution));
        plan.Select(PreparationStage.RootResolution);
        Assert.Throws<InvalidOperationException>(() => plan.Select(PreparationStage.EncodingObjectIndex));
        foreach (var roles in new string[][] { ["root", "install"], ["encoding", "root", "install", "root"], ["encoding", "root", "install", "payload"] })
            Assert.Throws<ArgumentException>(() => new PreparationProgressPlan(_ => Task.CompletedTask, default).ConfigureCached(roles));
    }

    [Fact]
    public async Task Declared_empty_download_and_size_manifests_keep_their_real_resolution_and_save_phases()
    {
        await using var harness = await Harness.Create(true, true, emptyManifests: true);
        var values = new List<WorkspacePreparationProgress>();
        await harness.Preparer.PrepareWithProgressAsync(harness.Request,
            value => { values.Add(value); return Task.CompletedTask; }, default);
        Assert.Contains(values, value => value.Stage == "Resolving download inventory · 0 entries" && value.Step!.Index == 12);
        Assert.Contains(values, value => value.Stage == "Saving download inventory · 0 entries" && value.Step!.Index == 13);
        Assert.Contains(values, value => value.Stage == "Resolving size inventory · 0 entries" && value.Step!.Index == 16);
        Assert.Contains(values, value => value.Stage == "Saving size inventory · 0 entries" && value.Step!.Index == 17);
        Assert.Equal(22, values[^1].Step!.Total);
    }

    [Theory]
    [InlineData(true, true, 22, 9)]
    [InlineData(true, false, 18, 8)]
    [InlineData(false, true, 18, 8)]
    [InlineData(false, false, 14, 7)]
    public async Task Real_preparation_emits_each_actual_number_and_offline_reopen_emits_its_own_plan(
        bool download, bool size, int freshTotal, int cachedTotal)
    {
        await using var harness = await Harness.Create(download, size);
        var values = new List<WorkspacePreparationProgress>();
        Task Report(WorkspacePreparationProgress value) { values.Add(value); return Task.CompletedTask; }
        var snapshot = await harness.Preparer.PrepareWithProgressAsync(harness.Request, Report, default);
        Assert.Equal("ready", snapshot.InventoryState);
        Assert.Null(values[0].Step!.Total);
        var numbered = values.Where(value => value.Step!.Total is not null).ToArray();
        Assert.Equal(Enumerable.Range(1, freshTotal), numbered.Select(value => value.Step!.Index).Distinct());
        Assert.Equal(numbered.Select(value => value.Step!.Index).Order(), numbered.Select(value => value.Step!.Index));
        Assert.All(numbered, value => Assert.Equal(freshTotal, value.Step!.Total));
        Assert.DoesNotContain(values, value => value.Stage.StartsWith("Resolving download", StringComparison.Ordinal) && !download);
        Assert.DoesNotContain(values, value => value.Stage.StartsWith("Resolving size", StringComparison.Ordinal) && !size);
        var root = Assert.Single(values, value => value.Stage == "Resolving root inventory");
        Assert.Equal(freshTotal - 4, root.Step!.Index);
        Assert.Contains(values, value => value.Step!.Index == 4 && value.Stage.StartsWith("Indexing encoding content", StringComparison.Ordinal));
        Assert.Contains(values, value => value.Step!.Index == 5 && value.Stage.StartsWith("Indexing encoding objects", StringComparison.Ordinal));
        values.Clear(); harness.BlockHttp = true;
        await harness.Preparer.PrepareWithProgressAsync(harness.Request, Report, default);
        numbered = values.Where(value => value.Step!.Total is not null).ToArray();
        Assert.Equal(Enumerable.Range(1, cachedTotal), numbered.Select(value => value.Step!.Index).Distinct());
        Assert.Equal(numbered.Select(value => value.Step!.Index).Order(), numbered.Select(value => value.Step!.Index));
        Assert.All(numbered, value => Assert.Equal(cachedTotal, value.Step!.Total));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-progress-" + Guid.NewGuid().ToString("N"));
        private DiscoveryCatalog catalog = null!;
        private VerifiedObjectStore objects = null!;
        private HttpClient client = null!;
        internal InventoryWorkspacePreparer Preparer { get; private set; } = null!;
        internal WorkspaceRequest Request { get; private set; } = null!;
        internal bool BlockHttp { get; set; }
        internal static async Task<Harness> Create(bool download, bool size, bool emptyManifests = false)
        {
            var result = new Harness();
            var fixture = InventoryFixture.Create();
            var configuration = Encoding.UTF8.GetString(fixture.BuildConfig);
            if (emptyManifests)
                foreach (var (role, decoded) in new (string Role, byte[] Decoded)[]
                {
                    ("download", [(byte)'D', (byte)'L', 1, 16, 0, 0, 0, 0, 0, 0, 0]),
                    ("size", [(byte)'D', (byte)'S', 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
                })
                {
                    byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded];
                    var ckey = Convert.ToHexStringLower(MD5.HashData(decoded));
                    var ekey = Convert.ToHexStringLower(MD5.HashData(encoded));
                    var oldSource = fixture.Metadata[role];
                    configuration = configuration.Replace($"{role} = {oldSource.ContentKey} {oldSource.EncodingKey}",
                        $"{role} = {ckey} {ekey}", StringComparison.Ordinal).Replace(
                        $"{role}-size = {oldSource.Decoded.Length} {oldSource.Encoded.Length}",
                        $"{role}-size = {decoded.Length} {encoded.Length}", StringComparison.Ordinal);
                    fixture.Responses[InventoryFixture.Url("data", ekey)] = encoded;
                }
            var lines = configuration.Split('\n').Where(line =>
                (download || !line.StartsWith("download", StringComparison.Ordinal)) &&
                (size || !line.StartsWith("size", StringComparison.Ordinal)));
            var config = Encoding.UTF8.GetBytes(string.Join('\n', lines));
            var buildKey = Convert.ToHexStringLower(MD5.HashData(config));
            fixture.Responses[InventoryFixture.Url("config", buildKey)] = config;
            var old = fixture.Discovery.Documents.Single(document => document.Role == "versions");
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(old.Bytes).Replace(fixture.BuildKey, buildKey, StringComparison.Ordinal));
            var document = old with { Bytes = bytes, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            var row = MetadataParser.Parse(bytes).Rows.Single();
            var identity = BuildIdentity.Create("wow", row.Fields);
            var observation = fixture.Discovery.Observations.Single() with { Identity = identity, OriginalFields = row.Fields };
            var discovery = fixture.Discovery with { Documents = [document, fixture.Discovery.Documents.Single(d => d.Role == "cdns")], Observations = [observation] };
            result.catalog = await DiscoveryCatalog.OpenAsync(result.directory);
            result.objects = new(result.directory);
            result.client = new(new Handler(result, fixture.Responses));
            result.Preparer = new(result.catalog, result.objects, result.client);
            result.Request = new("wow", identity.Id, "us");
            var g = await result.catalog.BeginRefreshAsync("wow", default);
            await result.catalog.CommitAsync("wow", g, discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            Preparer.Dispose(); client.Dispose(); await objects.DisposeAsync(); await catalog.DisposeAsync();
            Directory.Delete(directory, true);
        }
        private sealed class Handler(Harness harness, IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (harness.BlockHttp) throw new InvalidOperationException("Healthy saved preparation must not issue HTTP.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(responses[request.RequestUri!.AbsoluteUri]) });
            }
        }
    }
}
