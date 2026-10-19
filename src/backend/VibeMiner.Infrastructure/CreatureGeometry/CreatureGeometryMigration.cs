namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private void UpgradeCreatureGeometry()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE geometry_artifacts(artifact_id TEXT PRIMARY KEY,
                data_binding_sha256 TEXT NOT NULL UNIQUE,descriptor TEXT NOT NULL);
            CREATE TABLE geometry_source_locators(locator_key TEXT PRIMARY KEY,
                artifact_id TEXT NOT NULL,authority_sha256 TEXT NOT NULL,capsule TEXT NOT NULL,
                FOREIGN KEY(artifact_id) REFERENCES geometry_artifacts(artifact_id));
            PRAGMA user_version=14;
            """, transaction);
        transaction.Commit();
    }
}
