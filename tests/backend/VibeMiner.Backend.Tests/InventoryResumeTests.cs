using System.Net;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class InventoryResumeTests
{
    [Theory]
    [InlineData("Indexing encoding objects")]
    [InlineData("Indexing root metadata ·")]
    [InlineData("Resolving root inventory · 0")]
    [InlineData("Verifying complete catalog index")]
    public async Task Restart_reuses_completed_steps_and_preserves_exact_catalog(string interruptAt)
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        var interrupted = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Preparer.PrepareWithProgressAsync(h.Request, value =>
        {
            if (value.Stage.StartsWith(interruptAt, StringComparison.Ordinal)) { interrupted = true; cancel.Cancel(); }
            return Task.CompletedTask;
        }, cancel.Token));
        Assert.True(interrupted);
        Assert.Equal(0, h.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
        Assert.Equal(0, h.Scalar("SELECT count(*) FROM open_acknowledgements"));
        var checkpointCount = h.Scalar("SELECT count(*) FROM inventory_checkpoints");
        Assert.True(checkpointCount > 0);
        var generation = h.Scalar("SELECT id FROM inventory_generations");
        var requests = h.Handler.Requests;
        await h.Restart();
        var events = new List<WorkspacePreparationProgress>();
        var result = await h.Preparer.PrepareWithProgressAsync(h.Request, value => { events.Add(value); return Task.CompletedTask; }, default);
        Assert.Equal(generation.ToString(), result.Inventory!.Generation);
        Assert.Equal("8", result.Inventory.MembershipCount);
        Assert.Equal(h.Fixture.ExpectedEncodedBytes.ToString(), result.Inventory.TotalEncodedBytes);
        Assert.Equal(h.Fixture.ExpectedDecodedBytes.ToString(), result.Inventory.LogicalDecodedBytes);
        Assert.Equal(1, h.Scalar("SELECT count(*) FROM inventory_generations"));
        Assert.Equal(8, h.Scalar("SELECT count(DISTINCT id) FROM inventory_origins"));
        Assert.Equal(7, h.Handler.Requests); // completed acquisition is never downloaded a second time
        Assert.DoesNotContain(events, value => value.Step?.Index == 2);
        if (interruptAt != "Indexing encoding objects")
            Assert.Contains(events, value => value.Stage == "Using saved encoding index");
        if (interruptAt == "Verifying complete catalog index")
        {
            Assert.Equal(requests, h.Handler.Requests);
            Assert.Contains(events, value => value.Stage == "Using saved root inventory resolution");
            Assert.DoesNotContain(events, value => value.Stage.StartsWith("Resolving root inventory ·", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Same_count_checkpoint_corruption_refuses_reuse_without_deleting_evidence()
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Preparer.PrepareAsync(h.Request, stage =>
        { if (stage == "Indexing root metadata") cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        h.Execute("UPDATE en_content SET bytes=bytes+1");
        var count = h.Scalar("SELECT count(*) FROM en_content");
        await h.Restart();
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Preparer.PrepareAsync(h.Request, _ => Task.CompletedTask, default));
        Assert.Equal("LOCAL_CHECKPOINT_CORRUPT", error.Code);
        Assert.Equal(count, h.Scalar("SELECT count(*) FROM en_content"));
        Assert.Equal(0, h.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
    }

    [Fact]
    public async Task Missing_cached_source_refuses_checkpoint_reuse_without_redownload_or_ready_data()
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Preparer.PrepareAsync(h.Request, stage =>
        { if (stage == "Indexing root metadata") cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        h.DeleteEncodingObject();
        await h.Restart();
        var requests = h.Handler.Requests;
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Preparer.PrepareAsync(h.Request, _ => Task.CompletedTask, default));
        Assert.Equal("LOCAL_OBJECT_MISSING", error.Code);
        Assert.Equal(requests, h.Handler.Requests);
        Assert.Equal(0, h.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
    }

    [Theory]
    [InlineData("UPDATE inventory_units SET bytes=bytes+1")]
    [InlineData("UPDATE inventory_logical SET bytes=bytes+1")]
    [InlineData("INSERT INTO inventory_units(g,ekey,bytes) SELECT id,'ffffffffffffffffffffffffffffffff',123 FROM inventory_generations")]
    [InlineData("INSERT INTO inventory_logical(g,ckey,bytes) SELECT id,'ffffffffffffffffffffffffffffffff',123 FROM inventory_generations")]
    public async Task Resume_cannot_publish_inconsistent_or_orphan_derived_ledger_rows(string corruption)
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Preparer.PrepareAsync(h.Request, stage =>
        { if (stage == "Verifying complete catalog index") cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        h.Execute(corruption);
        await h.Restart();
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Preparer.PrepareAsync(h.Request, _ => Task.CompletedTask, default));
        Assert.Equal("CHECKPOINT_INTEGRITY", error.Code);
        Assert.Equal(0, h.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
        Assert.Equal(0, h.Scalar("SELECT count(*) FROM open_acknowledgements"));
    }

    [Fact]
    public async Task Root_batch_and_resume_position_commit_together_and_failed_batch_does_not_advance()
    {
        await using var h = await Harness.Create();
        var context = await h.Catalog.ResolveOpeningAsync(h.Request, default);
        var generation = await h.Catalog.StartInventoryAsync(context, default);
        var content = new string('1', 32); var encoded = new string('a', 32);
        await h.Catalog.AddEncodingAsync(generation, [new(content, 20, [encoded])], [new(encoded, 12)], default);
        var origins = Enumerable.Range(1, 300).Select(i => new InventoryOrigin(i, "root", null, i.ToString(),
            content, null, 0, null, null, "0", "0", null, "authored")).ToArray();
        await h.Catalog.AddOriginsAsync(generation, origins, default);
        var batch = await h.Catalog.ResolveOriginBatchAsync(generation, "root", 0, default);
        Assert.Equal((256, 256L), batch);
        await h.Restart();
        Assert.Equal((256L, 256L), await h.Catalog.ResolutionPositionAsync(generation, "root", default));
        h.Execute("UPDATE inventory_origins SET ckey='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' WHERE id=280");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ResolveOriginBatchAsync(generation, "root", 256, default));
        Assert.Equal((256L, 256L), await h.Catalog.ResolutionPositionAsync(generation, "root", default));
        Assert.Equal(256, h.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
        h.Execute("UPDATE inventory_origins SET ckey='11111111111111111111111111111111' WHERE id=280");
        batch = await h.Catalog.ResolveOriginBatchAsync(generation, "root", 256, default);
        Assert.Equal((44, 300L), batch);
        Assert.Equal((300L, 300L), await h.Catalog.ResolutionPositionAsync(generation, "root", default));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        internal InventoryFixture Fixture { get; } = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        private VerifiedObjectStore objects = null!;
        private HttpClient client = null!;
        internal Handler Handler { get; private set; } = null!;
        internal InventoryWorkspacePreparer Preparer { get; private set; } = null!;
        internal WorkspaceRequest Request => new("wow", Fixture.BuildIdentity, "us");
        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Handler = new(h.Fixture); h.client = new(h.Handler);
            await h.Start();
            var refresh = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", refresh, h.Fixture.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }
        private async Task Start()
        { Catalog = await DiscoveryCatalog.OpenAsync(directory.Path); objects = new(directory.Path); Preparer = new(Catalog, objects, client); }
        internal async Task Restart()
        { Preparer.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); await Start(); }
        internal void Execute(string sql)
        { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal long Scalar(string sql)
        { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        internal void DeleteEncodingObject()
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT receipt FROM metadata_objects WHERE ekey=$e";
            command.Parameters.AddWithValue("$e", Fixture.Metadata["encoding"].EncodingKey);
            var receipt = System.Text.Json.JsonSerializer.Deserialize<MetadataReceipt>((string)command.ExecuteScalar()!,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            File.Delete(Path.Combine(directory.Path, "objects-v1", receipt.Sha256[..2], receipt.Sha256[2..]));
        }
        private SqliteConnection Open()
        { var db = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False"); db.Open(); return db; }
        public async ValueTask DisposeAsync()
        { Preparer.Dispose(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); directory.Dispose(); }
    }
    private sealed class Handler(InventoryFixture fixture) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(fixture.Responses[request.RequestUri!.AbsoluteUri]) });
        }
    }
}
