namespace VibeMiner.Backend.Tests;

internal static class CatalogMigrationFixture
{
    // Synthetic old-schema cohorts must remove every newer table before setting their
    // historical user_version. Production migration never drops these tables.
    internal const string RemoveV14 = "DROP TABLE geometry_source_locators; DROP TABLE geometry_artifacts;";
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
