namespace VibeMiner.Formats;

public sealed record Db2Limits(long MaxDecodedBytes=256L<<20,long MaxRows=1_000_000,int MaxFields=256,
    int MaxSections=128,int MaxRecordBytes=65536,int MaxAuxiliaryBytes=32<<20,int MaxStringBytes=65536,
    long MaxIndexBytes=128L<<20,int MaxColumns=512,int MaxCopyDepth=64,bool AllowPartialEncryptedSections=false,
    bool AllowRawTextEvidence=false,long MaxRawTextEvidenceBytes=16L<<20,long MaxRelationshipBytes=16L<<20,int MaxRelationshipEntries=1_000_000,string? DefinitionSha256=null,IReadOnlyList<Db2RawIntegerRule>? VerifiedRawIntegerRules=null);
/// <summary>Trusted adapter scope for one exact provider column. The caller verifies the original definition bytes.
/// <see cref="NarrowsWhenConstant"/> (D108): when the column's values that don't fit the definition width all share one upper-bit
/// pattern, it narrows like any D087 column instead; with two or more it keeps the raw evidence and is never refused.</summary>
public sealed record Db2RawIntegerRule(string ExpectedDefinitionSha256,string TableHash,string LayoutHash,int FieldOrdinal,
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
/// <summary>What the header declared. <see cref="Format"/> is the header magic: "WDC3", "WDC4" or "WDC5".
/// <see cref="SchemaVersion"/> is the WDC5 schema version (always 5); WDC3 and WDC4 headers have none, so it is 0.</summary>
public sealed record Db2TableHeader(uint SchemaVersion,string TableHash,string LayoutHash,long DeclaredRecords,
    long DeclaredCopyRecords,int RecordSize,IReadOnlyList<Db2Column> Columns,string Format);
public sealed record Db2Completion(long DeclaredRecords,long PhysicalRows,long CopyRows,int ExcludedEncryptedSections,
    long ExcludedPhysicalRecords,long ExcludedCopyRecords,string Scope,long ConsumedBytes,long TextIssueCount=0);
public interface IDb2Consumer
{
    ValueTask HeaderAsync(Db2TableHeader header,CancellationToken token);
    // Callbacks remain provisional until complete framing/EOF and separate upstream source proof.
    ValueTask RowAsync(Db2Row row,CancellationToken token);
}

/// <summary>The layer 5 input until layer 4's ranged reads land (#172): an immutable decoded DB2 file of
/// <see cref="Length"/> bytes with named unreadable ranges. <see cref="ReadRangeAsync"/> fills the whole destination
/// or throws; the reader never asks for a range that touches a hole.</summary>
public interface IDb2ByteSource
{
    long Length{get;}
    IReadOnlyList<Db2Hole> Holes{get;}
    ValueTask ReadRangeAsync(long offset,Memory<byte> destination,CancellationToken token);
}
/// <summary>A named unreadable range of the decoded file, such as an encrypted BLTE chunk.</summary>
public sealed record Db2Hole(long Offset,long Length,string Name);

/// <summary>One section as declared at open. <see cref="Key"/> is the section key as 16 upper-case hex digits;
/// a nonzero key means the section is keyed and its records are not readable. Of a readable section's declared copies,
/// <see cref="ProtectedCopyCount"/> are excluded because their chain ends at an encrypted ID, and
/// <see cref="UnresolvedCopyCount"/> have a broken chain and throw when read.</summary>
public sealed record Db2Section(int Index,int RecordCount,int CopyCount,string Key,bool IsEncrypted,int ProtectedCopyCount=0,int UnresolvedCopyCount=0);

/// <summary>The named outcome of <see cref="Db2Table.FindAsync"/>.</summary>
public abstract record Db2Lookup
{
    private Db2Lookup(){}
    /// <summary>A readable physical row or a copy resolved to one.</summary>
    public sealed record Found(Db2Row Row):Db2Lookup;
    /// <summary>The ID is in a keyed section's encrypted-ID list, or is a copy whose chain ends at one.
    /// <see cref="ProtectedId"/> is that encrypted-list ID, and <see cref="Section"/> and <see cref="Key"/> are its
    /// keyed section: for a copy, ProtectedId differs from the looked-up ID and the copy itself lives in a readable section.</summary>
    public sealed record Protected(int Section,string Key,uint ProtectedId):Db2Lookup;
    /// <summary>The table has no keyed sections and no row or copy has this ID.</summary>
    public sealed record Absent:Db2Lookup;
    /// <summary>The table has keyed sections and no readable row or copy has this ID: absence is not proven.</summary>
    public sealed record NotInReadableSections:Db2Lookup;
}
