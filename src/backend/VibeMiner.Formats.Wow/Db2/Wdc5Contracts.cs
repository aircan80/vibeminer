namespace VibeMiner.Formats.Wow;

public sealed record Wdc5Limits(long MaxDecodedBytes=256L<<20,long MaxRows=1_000_000,int MaxFields=256,
    int MaxSections=128,int MaxRecordBytes=65536,int MaxAuxiliaryBytes=32<<20,int MaxStringBytes=65536,
    long MaxIndexBytes=128L<<20,int MaxColumns=512,int MaxCopyDepth=64,bool AllowPartialEncryptedSections=false,
    bool AllowRawTextEvidence=false,long MaxRawTextEvidenceBytes=16L<<20,long MaxRelationshipBytes=16L<<20,int MaxRelationshipEntries=1_000_000,string? DefinitionSha256=null,IReadOnlyList<Wdc5RawIntegerRule>? VerifiedRawIntegerRules=null);
/// <summary>Trusted adapter scope for one exact provider column. The caller verifies the original definition bytes.
/// <see cref="NarrowsWhenConstant"/> (D108): when the column's values that don't fit the definition width all share one upper-bit
/// pattern, it narrows like any D087 column instead; with two or more it keeps the raw evidence and is never refused.</summary>
public sealed record Wdc5RawIntegerRule(string ExpectedDefinitionSha256,string TableHash,string LayoutHash,int FieldOrdinal,
    string FieldName,uint StorageKind,int PhysicalBits,int CandidateBits,bool CandidateUnsigned,bool NarrowsWhenConstant=false);
public sealed record Db2ValueInterpretation(string Policy,int PhysicalBits,bool PhysicalSigned,
    int CandidateBits,bool CandidateUnsigned,string Warning);
/// <summary>D079: a verified integer field whose 32-bit pallet or common values don't all fit its definition width keeps the low
/// <see cref="UsedBits"/> bits of each value, signed or unsigned per the definition.</summary>
public sealed record Db2ValueNarrowing(int StoredBits,int UsedBits);
// Narrowing is omitted when null so descriptors written before D079 serialize to the same bytes.
public sealed record Db2Column(string Name,string Kind,bool IsId,Db2ValueInterpretation? Interpretation=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]Db2ValueNarrowing? Narrowing=null);
public sealed record Db2TextIssue(int ColumnIndex,string Code,string RawBytesHex);
public sealed record Db2Row(uint Id,bool IsCopy,uint? CopySourceId,IReadOnlyList<string> Values,IReadOnlyList<Db2TextIssue>? TextIssues=null);
public sealed record Wdc5TableHeader(uint SchemaVersion,string TableHash,string LayoutHash,long DeclaredRecords,
    long DeclaredCopyRecords,int RecordSize,IReadOnlyList<Db2Column> Columns);
public sealed record Wdc5Completion(long DeclaredRecords,long PhysicalRows,long CopyRows,int ExcludedEncryptedSections,
    long ExcludedPhysicalRecords,long ExcludedCopyRecords,string Scope,long ConsumedBytes,long TextIssueCount=0);
public interface IWdc5Consumer
{
    ValueTask HeaderAsync(Wdc5TableHeader header,CancellationToken token);
    // Callbacks remain provisional until complete framing/EOF and separate upstream source proof.
    ValueTask RowAsync(Db2Row row,CancellationToken token);
}
