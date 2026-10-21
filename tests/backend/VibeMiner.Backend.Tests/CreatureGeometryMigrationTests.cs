using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureGeometryMigrationTests
{
    private const string RemoveGeometry = CatalogMigrationFixture.RemoveV15 + "DROP TABLE IF EXISTS geometry_source_locators; DROP TABLE IF EXISTS geometry_artifacts; PRAGMA user_version=13;";

    [Fact]
    public async Task V13_upgrade_preserves_existing_feature_authority_without_backfill()
    {
        await using var h = await Harness.Create();
        await h.Execute("INSERT INTO creatures_artifacts VALUES('old-creature','original-creature-proof');" +
            "INSERT INTO item_effects_bridge_artifacts VALUES('old-effects','original-effects-proof');" + RemoveGeometry);
        await h.Restart();
        Assert.Equal(17L, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM creatures_artifacts WHERE descriptor='original-creature-proof'"));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM item_effects_bridge_artifacts WHERE descriptor='original-effects-proof'"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_artifacts"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task Failed_second_create_rolls_back_first_table_and_version_then_retry_is_clean()
    {
        await using var h = await Harness.Create();
        await h.Execute(RemoveGeometry + "CREATE TABLE geometry_source_locators(authored_collision TEXT);");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Restart());
        Assert.Equal(13L, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='geometry_artifacts'"));
        await h.Execute("DROP TABLE geometry_source_locators;"); await h.Restart();
        Assert.Equal(17L, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task Reopen_preserves_first_descriptors_and_context_locators()
    {
        await using var h = await Harness.Create();
        await h.Execute("INSERT INTO geometry_artifacts VALUES('artifact','binding','first-descriptor');" +
            "INSERT INTO geometry_source_locators VALUES('context-one','artifact','authority-one','original-capsule-one');" +
            "INSERT INTO geometry_source_locators VALUES('context-two','artifact','authority-two','original-capsule-two');");
        await h.Restart(); await h.Restart();
        Assert.Equal(17L, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_artifacts WHERE descriptor='first-descriptor'"));
        Assert.Equal(2L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task One_pure_binding_cannot_register_two_artifacts_and_orphan_locator_is_refused()
    {
        await using var h = await Harness.Create();
        await h.Execute("INSERT INTO geometry_artifacts VALUES('first','binding','first-descriptor');");
        await Assert.ThrowsAsync<SqliteException>(() => h.Execute("INSERT INTO geometry_artifacts VALUES('second','binding','second-descriptor');"));
        await Assert.ThrowsAsync<SqliteException>(() => h.Execute("INSERT INTO geometry_source_locators VALUES('context','absent','seal','capsule');"));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_artifacts"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task Durable_registry_has_no_lifetime_model_count_cap()
    {
        await using var h = await Harness.Create();
        await h.Execute("WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<300) " +
            "INSERT INTO geometry_artifacts SELECT 'artifact-'||v,'binding-'||v,'descriptor-'||v FROM n;" +
            "INSERT INTO geometry_source_locators SELECT 'context-'||artifact_id,artifact_id,'authority','capsule' FROM geometry_artifacts;");
        await h.Restart();
        Assert.Equal(300L, await h.Scalar("SELECT count(*) FROM geometry_artifacts"));
        Assert.Equal(300L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task Future_schema_is_refused_without_downgrade()
    {
        await using var h = await Harness.Create();
        await h.Execute("PRAGMA user_version=18;");
        Assert.Equal("CATALOG_VERSION", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Restart())).Code);
        Assert.Equal(18L, await h.Scalar("PRAGMA user_version"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-geometry-migration-" + Guid.NewGuid().ToString("N"));
        private DiscoveryCatalog catalog = null!;
        internal static async Task<Harness> Create()
        { var h = new Harness(); h.catalog = await DiscoveryCatalog.OpenAsync(h.directory); return h; }
        internal async Task Restart()
        { await catalog.DisposeAsync(); catalog = await DiscoveryCatalog.OpenAsync(directory); }
        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery(); return connection;
        }
        internal Task Execute(string sql)
        { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); return Task.CompletedTask; }
        internal Task<object?> Scalar(string sql)
        { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return Task.FromResult(command.ExecuteScalar()); }
        public async ValueTask DisposeAsync()
        { await catalog.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
