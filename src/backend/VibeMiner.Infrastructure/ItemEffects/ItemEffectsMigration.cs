namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private void UpgradeItemEffects()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE item_effects_bridge_artifacts(content_key TEXT PRIMARY KEY,descriptor TEXT NOT NULL);
            CREATE TABLE item_effects_bridge_admissions(build_id TEXT NOT NULL,region TEXT NOT NULL,
                source_reference TEXT NOT NULL,locale TEXT NOT NULL,content_key TEXT NOT NULL,prepared_at TEXT NOT NULL,
                PRIMARY KEY(build_id,region,source_reference,locale),
                FOREIGN KEY(build_id) REFERENCES builds(id),
                FOREIGN KEY(content_key) REFERENCES item_effects_bridge_artifacts(content_key));
            PRAGMA user_version=13;
            """, transaction);
        transaction.Commit();
    }
}
