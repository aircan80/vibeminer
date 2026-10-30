namespace VibeMiner.Domain;

// Provider definitions describe candidate interpretations, never selected-build file presence or rows.
public sealed record TableDefinitionProvider(string Name, string Revision, string ManifestSha256,
    string License, string SourceUrl);
public sealed record TableCatalogEntry(string TableId, string? Name, string? Db2FileDataId,
    string DefinitionState, string SourceState = "notChecked", string RowsState = "notPrepared");
public sealed record TableCatalogPage(string ProductId, string BuildIdentity, string SourceRegion,
    TableDefinitionProvider Provider, string Query, IReadOnlyList<TableCatalogEntry> Entries,
    string TotalCount, string? NextCursor);
public sealed record TableReference(string Table, string Column);
public sealed record TableDefinitionField(string Name, string Family, int? IntegerBits, bool? Unsigned,
    int ArrayLength, bool IsId, bool NonInline, bool RelationStorage, bool VerifiedName,
    string? Comment, TableReference? Reference);
public sealed record TableLayoutCandidate(string LayoutHash, IReadOnlyList<TableDefinitionField> Fields);
/// <summary>A definition fetched from the WoWDBDefs repository at runtime (D081): its commit and when these bytes were
/// first fetched. It is never bundled or verified, and it describes column layouts only, never game data.</summary>
public sealed record TableDefinitionFetched(string Revision, DateTimeOffset FetchedAt);
public sealed record TableDefinitionInfo(string Sha256, string SourceUrl,
    IReadOnlyList<TableDefinitionField> Columns, IReadOnlyList<TableLayoutCandidate> Candidates,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    TableDefinitionFetched? Fetched = null);
/// <summary>The last time WoWDBDefs was asked about a table this build's bundled definition doesn't list (D081, #81):
/// the fetched definition the cache holds, which may not cover the build either, the failure code when that last attempt
/// failed (failures are kept only while the engine runs), and when opening the table's data asks WoWDBDefs again.</summary>
public sealed record TableDefinitionCheck(DateTimeOffset CheckedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    TableDefinitionFetched? Fetched,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Failure,
    DateTimeOffset NextCheckAfter);
public sealed record TablesProblem(string Code, string Message);
/// <summary>DefinitionFetch is "on" when a table this build's bundled definitions don't cover may fetch a newer
/// definition from WoWDBDefs when its data opens (D081), "off" when the setting is off.</summary>
public sealed record TableDefinitionSnapshot(string ProductId, string BuildIdentity, string SourceRegion,
    string BuildVersion, TableDefinitionProvider Provider, TableCatalogEntry Entry,
    TableDefinitionInfo? Definition, TablesProblem? Problem,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DefinitionFetch = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    TableDefinitionCheck? DefinitionCheck = null);
