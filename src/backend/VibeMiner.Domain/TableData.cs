namespace VibeMiner.Domain;

public sealed record TableColumnInterpretation(string Policy, int PhysicalBits, bool PhysicalSigned,
    int CandidateBits, bool CandidateUnsigned, string Warning);
/// <summary>D079: the column's 32-bit pallet or common values are wider than its definition; each value keeps its low
/// <see cref="UsedBits"/> bits, signed or unsigned per the definition.</summary>
public sealed record TableColumnNarrowing(int StoredBits, int UsedBits);
// Narrowing is left off the wire when null, so every other column keeps its exact shape.
public sealed record TableDataColumn(string Name, string Family, bool IsId, TableReference? Reference,
    TableColumnInterpretation? Interpretation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    TableColumnNarrowing? Narrowing = null);
public sealed record TableDataCoverage(string Scope, string DeclaredRecords, string PhysicalRows,
    string CopyRows, string ExcludedKeyedSections, string ExcludedDeclaredRecords,
    string DeclaredCopyRecords, string ExcludedPhysicalRecords, string ExcludedCopyRecords);
public sealed record TableDataSnapshot(string ProductId, string BuildIdentity, string SourceRegion,
    string TableId, string ResearchLocale, string ArtifactId, string PreparedAt,
    string LayoutHash, string DefinitionSha256, string ProviderRevision, string DecoderRevision,
    ContentReceipt Source, SelectedFileAssociation Association, IReadOnlyList<TableDataColumn> Columns,
    TableDataCoverage Coverage, string RowCount, string TextIssueCount = "0");
public sealed record TableTextIssue(int ColumnIndex, string Code, string RawBytesHex);
public sealed record TableDataRow(string Id, bool IsCopy, string? CopySourceId, IReadOnlyList<string> Values,
    IReadOnlyList<TableTextIssue>? TextIssues = null);
public sealed record TableDataReadiness(TableDataSnapshot? Snapshot);
public sealed record TableDataPage(string ProductId, string BuildIdentity, string SourceRegion,
    string TableId, string ResearchLocale, string ArtifactId, IReadOnlyList<TableDataRow> Rows,
    string TotalCount, string? NextCursor);
