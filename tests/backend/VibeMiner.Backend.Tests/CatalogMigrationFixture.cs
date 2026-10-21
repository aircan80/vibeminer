namespace VibeMiner.Backend.Tests;

internal static class CatalogMigrationFixture
{
    // Synthetic old-schema cohorts must remove every newer table before setting their
    // historical user_version. Production migration never drops these tables.
    // The step to version 17 added the Files columns, indexes and tables (D068); the step to 16 adds nothing.
    internal const string RemoveV17 = """
        DROP INDEX inventory_root_files; DROP INDEX inventory_root_variant_files; DROP INDEX inventory_stored_files;
        DROP INDEX inventory_install_names; DROP INDEX inventory_install_files;
        DROP INDEX inventory_content; CREATE INDEX inventory_content ON inventory_origins(g,ckey,family);
        DROP TABLE inventory_root_variants; DROP TABLE inventory_file_stats;
        ALTER TABLE inventory_origins DROP COLUMN fdid_n; ALTER TABLE inventory_origins DROP COLUMN locale_n;
        ALTER TABLE inventory_origins DROP COLUMN flags_n; ALTER TABLE inventory_origins DROP COLUMN grp;
        ALTER TABLE inventory_origins DROP COLUMN stored;
        """;
    internal const string RemoveV16 = RemoveV17;
    internal const string RemoveV15 = RemoveV16 + "DROP TABLE root_scopes; DROP TABLE stored_pages; DROP TABLE stored_coverage; DROP TABLE object_absence; DROP TABLE stored_changes; DROP INDEX IF EXISTS encoded_objects_sha; DROP INDEX IF EXISTS content_objects_sha;";
    internal const string RemoveV14 = RemoveV15 + "DROP TABLE geometry_source_locators; DROP TABLE geometry_artifacts;";
    internal const string RemoveV13 = RemoveV14 + "DROP TABLE item_effects_bridge_admissions; DROP TABLE item_effects_bridge_artifacts;";
    internal const string RemoveV12 = RemoveV13 + "DROP TABLE creatures_admissions; DROP TABLE creatures_artifacts;";
    internal const string RemoveV11 = RemoveV12 + "DROP TABLE item_classification_admissions; DROP TABLE item_classification_artifacts;";
    internal const string RemoveV10 = RemoveV11 + "DROP TABLE item_icon_admissions; DROP TABLE icon_artifacts;";
    internal const string RemoveV9 = RemoveV10 + """
        DROP TABLE items_admissions; DROP TABLE items_artifacts;
        DROP TABLE retention_problems; DROP TABLE retention_bits; DROP TABLE retention_coverage;
        DROP TABLE retention_jobs; DROP TABLE retention_sequence; DROP TABLE retention_native_sources;
        DROP TABLE object_invalidity; DROP TABLE object_invalidity_sequence;
        """;
}
