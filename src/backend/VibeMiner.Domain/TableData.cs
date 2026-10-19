namespace VibeMiner.Domain;

public sealed record TableColumnInterpretation(string Policy, int PhysicalBits, bool PhysicalSigned,
    int CandidateBits, bool CandidateUnsigned, string Warning);
public sealed record TableDataColumn(string Name, string Family, bool IsId, TableReference? Reference,
    TableColumnInterpretation? Interpretation = null);
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
