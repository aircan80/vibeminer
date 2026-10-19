using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class InventoryIntegrityTests
{
    private static readonly string[] Tables = ["en_content", "en_candidates", "en_objects", "inventory_origins", "inventory_tags", "inventory_logical", "inventory_units", "inventory_available"];

    [Theory]
    [InlineData("UPDATE en_content SET bytes=bytes+1 WHERE g=1")]
    [InlineData("UPDATE en_candidates SET ekey='different' WHERE g=1")]
    [InlineData("UPDATE en_objects SET bytes=bytes+1 WHERE g=1")]
    [InlineData("UPDATE inventory_origins SET name='changed' WHERE g=1")]
    [InlineData("UPDATE inventory_origins SET raw='changed' WHERE g=1")]
    [InlineData("UPDATE inventory_origins SET flags='changed' WHERE g=1")]
    [InlineData("UPDATE inventory_origins SET encoded=encoded+1 WHERE g=1")]
    [InlineData("UPDATE inventory_tags SET bits=x'81' WHERE g=1")]
    [InlineData("UPDATE inventory_logical SET bytes=bytes+1 WHERE g=1")]
    [InlineData("UPDATE inventory_units SET bytes=bytes+1 WHERE g=1")]
    public async Task SameCountImmutableCorruptionChangesCanonicalDigest(string mutation)
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "ready", 1);
        var before = await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None);
        fixture.Execute(mutation);
        Assert.NotEqual(before, await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None));
        Assert.Equal(1L, fixture.Count("inventory_origins", 1));
        Assert.Equal(1L, fixture.Count("inventory_units", 1));
    }

    [Fact]
    public async Task DigestUsesCanonicalKeyOrderAcrossManySlicesAndExcludesMutableAssessment()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "ready", 300);
        var before = await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None);
        foreach (var table in Tables)
        {
            fixture.Execute($"CREATE TEMP TABLE reordered AS SELECT * FROM {table} WHERE g=1 ORDER BY rowid DESC; DELETE FROM {table} WHERE g=1; INSERT INTO {table} SELECT * FROM reordered; DROP TABLE reordered;");
        }
        fixture.Execute("UPDATE inventory_units SET retained=1,shared=1 WHERE g=1; DELETE FROM inventory_available WHERE g=1;");
        Assert.Equal(before, await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task NullEmptyTextAndBlobValuesHaveDistinctFraming()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "ready", 1);
        var digests = new HashSet<string>();
        foreach (var value in new[] { "NULL", "''", "x''", "'abc'", "x'616263'" })
        {
            fixture.Execute("UPDATE inventory_origins SET name=" + value + " WHERE g=1;");
            Assert.True(digests.Add(await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None)));
        }
    }

    [Fact]
    public async Task DigestRejectsUnboundedStoredFieldsAndUnexpectedRealValues()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "ready", 1);
        fixture.Execute("UPDATE inventory_tags SET bits=zeroblob(2097153) WHERE g=1;");
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None))).Code);
        fixture.Execute("UPDATE inventory_tags SET bits=x'80' WHERE g=1; UPDATE en_content SET bytes=1.25 WHERE g=1;");
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task CleanupRetiresAllStagingChildrenInBatchesAndKeepsReadySources()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "ready", 3);
        fixture.Seed(2, "staging", 1300);
        fixture.Seed(3, "abandoned", 513);
        fixture.Execute("INSERT INTO metadata_objects VALUES('retained-source','content',9,'original receipt');");
        var digest = await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None);
        await fixture.Catalog.CleanupStagingInventoriesAsync(CancellationToken.None);
        foreach (var table in Tables)
        {
            Assert.Equal(3L, fixture.Count(table, 1));
            Assert.Equal(0L, fixture.Count(table, 2));
            Assert.Equal(0L, fixture.Count(table, 3));
        }
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM inventory_generations"));
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM metadata_objects"));
        Assert.Equal(digest, await fixture.Catalog.ComputeInventoryDigestAsync(1, CancellationToken.None));
        await fixture.Catalog.CleanupStagingInventoriesAsync(CancellationToken.None);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM inventory_generations"));
    }

    [Fact]
    public async Task Legacy_cleanup_reports_measured_rows_and_keeps_checkpointed_work()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "ready", 2);
        fixture.Seed(2, "abandoned", 9000);
        fixture.Seed(3, "abandoned", 3);
        fixture.Execute("INSERT INTO inventory_checkpoints VALUES(3,'encoding-content','inventory-checkpoint-v1',NULL,'{}','authored','2026-10-18T12:00:00Z')");
        var updates = new List<string>();
        await fixture.Catalog.CleanupStagingInventoriesAsync(default, value => { updates.Add(value); return Task.CompletedTask; });
        Assert.NotEmpty(updates);
        Assert.Contains(updates, value => value.StartsWith("Cleaning up unfinished catalog data · 0 of ", StringComparison.Ordinal));
        Assert.EndsWith("100%", updates[^1]);
        Assert.All(updates, value => Assert.DoesNotContain("Retiring", value));
        Assert.Equal(0, fixture.Count("inventory_origins", 2));
        Assert.Equal(3, fixture.Count("inventory_origins", 3));
        Assert.Equal(2, fixture.Count("inventory_origins", 1));
    }

    [Fact]
    public async Task CancellationBeforeAdmissionPreservesStagingForLaterCleanup()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        fixture.Seed(1, "staging", 600);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.ComputeInventoryDigestAsync(1, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.CleanupStagingInventoriesAsync(canceled.Token));
        Assert.Equal(600L, fixture.Count("inventory_origins", 1));
        await fixture.Catalog.CleanupStagingInventoriesAsync(CancellationToken.None);
        Assert.Equal(0L, fixture.Count("inventory_origins", 1));
    }

    private sealed class IntegrityFixture : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        private SqliteConnection database = null!;
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        private string build = "";
        public static async Task<IntegrityFixture> CreateAsync()
        {
            var fixture = new IntegrityFixture();
            fixture.Catalog = await DiscoveryCatalog.OpenAsync(fixture.directory.Path);
            var source = InventoryFixture.Create();
            var refresh = await fixture.Catalog.BeginRefreshAsync("wow", CancellationToken.None);
            await fixture.Catalog.CommitAsync("wow", refresh, source.Discovery, "2026-10-18T12:00:00Z", CancellationToken.None);
            fixture.build = source.BuildIdentity;
            fixture.database = new SqliteConnection($"Data Source={Path.Combine(fixture.directory.Path, "discovery-v1.sqlite3")};Pooling=False");
            fixture.database.Open();
            return fixture;
        }
        public void Seed(long generation, string state, int count)
        {
            using var tx = database.BeginTransaction();
            using var header = database.CreateCommand(); header.Transaction = tx;
            header.CommandText = "INSERT INTO inventory_generations(id,build_id,region,state,profile,source) VALUES($g,$b,'us',$s,'authored-test','{}')";
            header.Parameters.AddWithValue("$g", generation); header.Parameters.AddWithValue("$b", build); header.Parameters.AddWithValue("$s", state); header.ExecuteNonQuery();
            var selects = new Dictionary<string, string>
            {
                ["en_content"] = "$g,printf('%032x',n),n",
                ["en_candidates"] = "$g,printf('%032x',n),0,printf('%032x',n)",
                ["en_objects"] = "$g,printf('%032x',n),n",
                ["inventory_origins"] = "$g,n,'root',NULL,CAST(n AS TEXT),printf('%032x',n),NULL,16,NULL,n,'1','0',NULL,'{}',printf('%032x',n),n,n",
                ["inventory_tags"] = "$g,'root',n,'tag',1,x'80'",
                ["inventory_logical"] = "$g,printf('%032x',n),n",
                ["inventory_units"] = "$g,printf('%032x',n),n,0,0",
                ["inventory_available"] = "$g,printf('%032x',n)"
            };
            foreach (var table in Tables)
            {
                using var command = database.CreateCommand(); command.Transaction = tx;
                command.CommandText = $"WITH RECURSIVE sequence(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM sequence WHERE n<$count) INSERT INTO {table} SELECT {selects[table]} FROM sequence";
                command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$g", generation); command.ExecuteNonQuery();
            }
            tx.Commit();
        }
        public void Execute(string sql) { using var command = database.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Scalar(string sql) { using var command = database.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public long Count(string table, long generation) => Scalar($"SELECT count(*) FROM {table} WHERE g={generation}");
        public async ValueTask DisposeAsync() { database.Dispose(); await Catalog.DisposeAsync(); directory.Dispose(); }
    }
}
