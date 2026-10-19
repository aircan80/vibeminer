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
public sealed record TableDefinitionInfo(string Sha256, string SourceUrl,
    IReadOnlyList<TableDefinitionField> Columns, IReadOnlyList<TableLayoutCandidate> Candidates);
public sealed record TablesProblem(string Code, string Message);
public sealed record TableDefinitionSnapshot(string ProductId, string BuildIdentity, string SourceRegion,
    string BuildVersion, TableDefinitionProvider Provider, TableCatalogEntry Entry,
    TableDefinitionInfo? Definition, TablesProblem? Problem);
