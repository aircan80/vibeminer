using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsMigrationTests
{
    private const string RemoveEffects = CatalogMigrationFixture.RemoveV14 + "DROP TABLE IF EXISTS item_effects_bridge_admissions; DROP TABLE IF EXISTS item_effects_bridge_artifacts; PRAGMA user_version=12;";

    [Fact]
    public async Task V12_upgrade_preserves_research_sources_and_paused_download_intent_without_backfill()
    {
        await using var h = await RetentionCatalogTests.Harness.Create();
        await h.RecordPayloads(); var saved = await h.Set(RetentionAction.Pause);
        await h.Execute("INSERT INTO items_artifacts VALUES('old-items','original-item-proof');" +
            "INSERT INTO icon_artifacts VALUES('old-icon','original-icon-proof');" +
            "INSERT INTO item_classification_artifacts VALUES('old-label','original-label-proof');" +
            "INSERT INTO creatures_artifacts VALUES('old-creature','original-creature-proof');" + RemoveEffects);
        await h.Restart();
        Assert.Equal(18, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(2, await h.Scalar("SELECT count(*) FROM encoded_objects"));
        foreach (var (table, descriptor) in new[] { ("items_artifacts", "original-item-proof"),
            ("icon_artifacts", "original-icon-proof"), ("item_classification_artifacts", "original-label-proof"),
            ("creatures_artifacts", "original-creature-proof") })
            Assert.Equal(1, await h.Scalar($"SELECT count(*) FROM {table} WHERE descriptor='{descriptor}'"));
        Assert.Equal(0, await h.Scalar("SELECT count(*) FROM item_effects_bridge_artifacts"));
        Assert.Equal(0, await h.Scalar("SELECT count(*) FROM item_effects_bridge_admissions"));
        var restored = await h.Read();
        Assert.Equal(saved.JobId, restored.JobId); Assert.Equal(saved.Revision, restored.Revision);
        Assert.Equal(RetentionState.Paused, restored.State); Assert.True(restored.Paused);
        Assert.Equal(RetentionMode.DownloadAll, restored.Mode);
    }

    [Fact]
    public async Task Current_schema_reopen_preserves_first_compact_descriptor_and_admission()
    {
        await using var h = await RetentionCatalogTests.Harness.Create();
        await h.Execute("INSERT INTO item_effects_bridge_artifacts VALUES('original-index','original-index-proof');" +
            "INSERT INTO item_effects_bridge_admissions SELECT id,'us','original-reference','enUS','original-index','original-time' FROM builds LIMIT 1;");
        await h.Restart(); await h.Restart();
        Assert.Equal(18, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(1, await h.Scalar("SELECT count(*) FROM item_effects_bridge_artifacts WHERE content_key='original-index' AND descriptor='original-index-proof'"));
        Assert.Equal(1, await h.Scalar("SELECT count(*) FROM item_effects_bridge_admissions WHERE content_key='original-index' AND source_reference='original-reference' AND prepared_at='original-time'"));
    }

    [Fact]
    public async Task Failed_second_table_creation_rolls_back_first_table_and_version_then_retries_cleanly()
    {
        await using var h = await RetentionCatalogTests.Harness.Create();
        await h.Execute(RemoveEffects + "CREATE TABLE item_effects_bridge_admissions(authored_collision TEXT);");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Restart());
        Assert.Equal(12, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(0, await h.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='item_effects_bridge_artifacts'"));
        Assert.Equal(1, await h.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='item_effects_bridge_admissions'"));
        await h.Execute("DROP TABLE item_effects_bridge_admissions");
        await h.Restart();
        Assert.Equal(18, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(0, await h.Scalar("SELECT count(*) FROM item_effects_bridge_artifacts"));
        Assert.Equal(0, await h.Scalar("SELECT count(*) FROM pragma_foreign_key_check"));
    }
}
