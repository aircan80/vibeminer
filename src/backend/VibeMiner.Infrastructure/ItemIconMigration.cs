namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private void UpgradeItemIcons()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE icon_artifacts(request_key TEXT PRIMARY KEY,descriptor TEXT NOT NULL);
            CREATE TABLE item_icon_admissions(feature_artifact TEXT NOT NULL,fdid INTEGER NOT NULL,
                descriptor TEXT NOT NULL,PRIMARY KEY(feature_artifact,fdid));
            PRAGMA user_version=10;
            """, transaction);
        transaction.Commit();
    }
}
